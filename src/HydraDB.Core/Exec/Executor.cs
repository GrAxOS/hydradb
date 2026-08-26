using HydraDB.Core.Catalog;
using HydraDB.Core.Index;
using HydraDB.Core.Sql;
using HydraDB.Core.Txn;

namespace HydraDB.Core.Exec;

/// <summary>Access path chosen for the most recent scan.</summary>
public enum ScanPlan
{
    None,
    HeapScalar,
    HeapVector,
    IndexRange
}

/// <summary>
/// Executes a parsed statement against a snapshot.
///
/// Reads run through a chunked pipeline: rows are batched into a <see cref="DataChunk"/> of
/// 1024 and filtered a chunk at a time. Three access paths, strongest first:
///   IndexRange  - the predicate bounds the INT64 primary key, so the B+Tree is walked.
///   HeapVector  - one INT comparison against a literal, evaluated with SIMD.
///   HeapScalar  - anything else, evaluated row by row by the interpreter.
///
/// Writes go through <see cref="BulkInsert"/>: every row is validated before any row is
/// written, so one statement is one transaction and one WAL commit, and a violation
/// anywhere in the batch leaves the heap and the index untouched.
/// </summary>
public sealed class Executor
{
    private readonly MvccStore _store;
    private readonly TableIndexSet? _indexes;

    public Executor(MvccStore store, TableIndexSet? indexes = null)
    {
        _store = store;
        _indexes = indexes;
    }

    /// <summary>Access path used by the most recent scan. Diagnostic only.</summary>
    public ScanPlan LastPlan { get; private set; } = ScanPlan.None;

    public QueryResult Run(Stmt statement, Transaction txn) => statement switch
    {
        CreateTableStmt s => CreateTable(s, txn),
        InsertStmt s => Insert(s, txn),
        SelectStmt s => Select(s, txn),
        UpdateStmt s => Update(s, txn),
        DeleteStmt s => Delete(s, txn),
        _ => throw new SqlException("statement cannot be executed at this level")
    };

    private QueryResult CreateTable(CreateTableStmt statement, Transaction txn)
    {
        foreach (ForeignKey fk in statement.Def.ForeignKeys)
            if (!_store.HasTable(fk.ReferencedTable))
                throw new SqlException($"referenced table '{fk.ReferencedTable}' does not exist");

        _store.CreateTable(statement.Def, txn);
        return QueryResult.Message($"table '{statement.Def.Name}' created");
    }

    // ---------------------------------------------------------------- writes

    private QueryResult Insert(InsertStmt statement, Transaction txn)
    {
        TableDef def = _store.GetTable(statement.Table).Def;
        var batch = new List<object?[]>(statement.Rows.Count);

        foreach (List<Expr> rowExprs in statement.Rows)
        {
            var values = new object?[def.Columns.Count];

            if (statement.Columns.Count == 0)
            {
                if (rowExprs.Count != def.Columns.Count)
                    throw new SqlException($"expected {def.Columns.Count} values but found {rowExprs.Count}");

                for (int i = 0; i < rowExprs.Count; i++)
                    values[i] = Coerce(def.Columns[i], Evaluator.Eval(rowExprs[i], def, values));
            }
            else
            {
                if (rowExprs.Count != statement.Columns.Count)
                    throw new SqlException("column and value counts differ");

                for (int i = 0; i < statement.Columns.Count; i++)
                {
                    int index = def.IndexOf(statement.Columns[i]);
                    if (index < 0) throw new SqlException($"unknown column '{statement.Columns[i]}'");
                    values[index] = Coerce(def.Columns[index], Evaluator.Eval(rowExprs[i], def, values));
                }
            }

            batch.Add(values);
        }

        return BulkInsert(def, batch, txn);
    }

    /// <summary>
    /// Atomic batch insert. Phase 1 validates every row, including duplicates that exist only
    /// inside the batch itself. Phase 2 writes. Nothing is written until everything validates,
    /// so the caller's transaction produces exactly one WAL commit.
    /// </summary>
    public QueryResult BulkInsert(TableDef def, List<object?[]> rows, Transaction txn)
    {
        int primaryKeyColumn = -1;
        for (int i = 0; i < def.Columns.Count; i++)
        {
            if (!def.Columns[i].PrimaryKey) continue;
            primaryKeyColumn = i;
            break;
        }

        var seen = primaryKeyColumn >= 0 ? new HashSet<object>(rows.Count) : null;

        foreach (object?[] values in rows)
        {
            if (seen is not null)
            {
                object? candidate = values[primaryKeyColumn];
                if (candidate is null)
                    throw new SqlException($"primary key '{def.Columns[primaryKeyColumn].Name}' cannot be null");
                if (!seen.Add(candidate))
                    throw new SqlException($"duplicate primary key in '{def.Name}'");
            }

            CheckPrimaryKey(def, values, txn, 0);
            CheckForeignKeys(def, values, txn);
        }

        foreach (object?[] values in rows)
        {
            long rowId = _store.Insert(def.Name, values, txn);
            IndexRow(def, rowId, values);
        }

        return QueryResult.Changed(rows.Count);
    }

    private QueryResult Update(UpdateStmt statement, Transaction txn)
    {
        TableDef def = _store.GetTable(statement.Table).Def;

        var targets = new List<RowVersion>();
        foreach (RowVersion version in _store.Scan(def.Name, txn))
            if (statement.Where is null || Evaluator.Truthy(Evaluator.Eval(statement.Where, def, version.Values)))
                targets.Add(version);

        int updated = 0;
        foreach (RowVersion version in targets)
        {
            var values = (object?[])version.Values.Clone();

            foreach ((string column, Expr expr) in statement.Assignments)
            {
                int index = def.IndexOf(column);
                if (index < 0) throw new SqlException($"unknown column '{column}'");
                values[index] = Coerce(def.Columns[index], Evaluator.Eval(expr, def, version.Values));
            }

            CheckPrimaryKey(def, values, txn, version.RowId);
            CheckForeignKeys(def, values, txn);

            _store.Update(def.Name, version, values, txn);
            IndexRow(def, version.RowId, values);
            updated++;
        }

        return QueryResult.Changed(updated);
    }

    private QueryResult Delete(DeleteStmt statement, Transaction txn)
    {
        TableDef def = _store.GetTable(statement.Table).Def;

        var targets = new List<RowVersion>();
        foreach (RowVersion version in _store.Scan(def.Name, txn))
            if (statement.Where is null || Evaluator.Truthy(Evaluator.Eval(statement.Where, def, version.Values)))
                targets.Add(version);

        foreach (RowVersion version in targets)
            _store.Delete(def.Name, version, txn);

        return QueryResult.Changed(targets.Count);
    }

    // ---------------------------------------------------------------- reads

    private QueryResult Select(SelectStmt statement, Transaction txn)
    {
        TableDef def = _store.GetTable(statement.Table).Def;

        if (statement.Aggregates.Count > 0) return Aggregate(statement, def, txn);

        var indices = new List<int>();
        var names = new List<string>();

        if (statement.Columns.Count == 1 && statement.Columns[0] == "*")
        {
            for (int i = 0; i < def.Columns.Count; i++)
            {
                indices.Add(i);
                names.Add(def.Columns[i].Name);
            }
        }
        else
        {
            foreach (string column in statement.Columns)
            {
                int index = def.IndexOf(column);
                if (index < 0) throw new SqlException($"unknown column '{column}'");
                indices.Add(index);
                names.Add(def.Columns[index].Name);
            }
        }

        var matched = new List<object?[]>();
        foreach (DataChunk chunk in Pipeline(def, statement.Where, txn))
            for (int i = 0; i < chunk.Selection.Count; i++)
                matched.Add(chunk.Rows[chunk.Selection.Indices[i]]);

        if (statement.OrderBy is not null)
        {
            int orderIndex = def.IndexOf(statement.OrderBy);
            if (orderIndex < 0) throw new SqlException($"unknown column '{statement.OrderBy}'");

            matched.Sort((left, right) =>
            {
                object? a = left[orderIndex];
                object? b = right[orderIndex];
                int comparison = a is null
                    ? (b is null ? 0 : -1)
                    : (b is null ? 1 : Evaluator.Compare(a, b));
                return statement.OrderDescending ? -comparison : comparison;
            });
        }

        var rows = new List<object?[]>();
        foreach (object?[] row in matched)
        {
            if (statement.Limit is int limit && rows.Count >= limit) break;

            var projected = new object?[indices.Count];
            for (int i = 0; i < indices.Count; i++) projected[i] = row[indices[i]];
            rows.Add(projected);
        }

        return new QueryResult { Columns = names, Rows = rows };
    }

    private QueryResult Aggregate(SelectStmt statement, TableDef def, Transaction txn)
    {
        int groupColumn = -1;
        string? groupName = null;

        if (statement.GroupBy is not null)
        {
            groupColumn = def.IndexOf(statement.GroupBy);
            if (groupColumn < 0) throw new SqlException($"unknown column '{statement.GroupBy}'");
            groupName = def.Columns[groupColumn].Name;
        }

        foreach (AggregateSpec spec in statement.Aggregates)
        {
            if (spec.Kind == AggregateKind.CountStar) continue;

            int index = def.IndexOf(spec.Column!);
            if (index < 0) throw new SqlException($"unknown column '{spec.Column}'");
            spec.ColumnIndex = index;
        }

        var aggregate = new HashAggregate(groupColumn, groupName, statement.Aggregates);
        return aggregate.Run(Pipeline(def, statement.Where, txn), estimatedKeys: 64);
    }

    /// <summary>Chooses the access path and returns the matching chunk stream.</summary>
    private IEnumerable<DataChunk> Pipeline(TableDef def, Expr? where, Transaction txn)
    {
        if (TryPlanIndexRange(def, where, out Table? indexed, out long low, out long high))
        {
            LastPlan = ScanPlan.IndexRange;
            return IndexRangeChunks(indexed!, low, high, txn);
        }

        if (TryPlanVectorFilter(def, where, out int column, out CompareOp op, out long threshold))
        {
            LastPlan = ScanPlan.HeapVector;
            return VectorChunks(def, column, op, threshold, txn);
        }

        LastPlan = ScanPlan.HeapScalar;
        return ScalarChunks(def, where, txn);
    }

    private bool TryPlanIndexRange(TableDef def, Expr? where, out Table? indexed, out long low, out long high)
    {
        indexed = null;
        low = long.MinValue;
        high = long.MaxValue;

        if (where is null || _indexes is null) return false;
        if (!_indexes.TryGetTable(def.Name, out Table table) || !table.HasPrimaryKey) return false;

        bool bounded = false;
        if (!CollectBounds(where, def, table.PrimaryKeyColumn, ref low, ref high, ref bounded)) return false;
        if (!bounded || low > high) return false;

        indexed = table;
        return true;
    }

    /// <summary>
    /// Folds a conjunction of primary-key comparisons into one closed range. Returns false as
    /// soon as anything else appears, because a leftover predicate would need re-checking.
    /// </summary>
    private static bool CollectBounds(Expr expr, TableDef def, int keyColumn, ref long low, ref long high, ref bool bounded)
    {
        if (expr is not Binary binary) return false;

        if (binary.Op == "and")
        {
            return CollectBounds(binary.Left, def, keyColumn, ref low, ref high, ref bounded)
                && CollectBounds(binary.Right, def, keyColumn, ref low, ref high, ref bounded);
        }

        if (!TryReadKeyComparison(binary, def, keyColumn, out string op, out long value)) return false;

        switch (op)
        {
            case "=":
                if (value > low) low = value;
                if (value < high) high = value;
                break;
            case ">=":
                if (value > low) low = value;
                break;
            case ">":
                if (value == long.MaxValue) return false;
                if (value + 1 > low) low = value + 1;
                break;
            case "<=":
                if (value < high) high = value;
                break;
            case "<":
                if (value == long.MinValue) return false;
                if (value - 1 < high) high = value - 1;
                break;
            default:
                return false;
        }

        bounded = true;
        return true;
    }

    /// <summary>Normalizes "key op literal" and "literal op key" into "key op literal".</summary>
    private static bool TryReadKeyComparison(Binary binary, TableDef def, int keyColumn, out string op, out long value)
    {
        op = binary.Op;
        value = 0;

        if (binary.Left is ColumnRef left && binary.Right is Literal literal)
        {
            if (def.IndexOf(left.Name) != keyColumn) return false;
            if (literal.Value is not long number) return false;
            value = number;
            return true;
        }

        if (binary.Left is Literal mirrored && binary.Right is ColumnRef right)
        {
            if (def.IndexOf(right.Name) != keyColumn) return false;
            if (mirrored.Value is not long number) return false;

            value = number;
            op = binary.Op switch
            {
                "<" => ">",
                "<=" => ">=",
                ">" => "<",
                ">=" => "<=",
                _ => binary.Op
            };
            return true;
        }

        return false;
    }

    private bool TryPlanVectorFilter(TableDef def, Expr? where, out int column, out CompareOp op, out long threshold)
    {
        column = -1;
        op = CompareOp.Equal;
        threshold = 0;

        if (where is not Binary binary) return false;

        string text;
        if (binary.Left is ColumnRef left && binary.Right is Literal literal)
        {
            column = def.IndexOf(left.Name);
            if (literal.Value is not long number) return false;
            threshold = number;
            text = binary.Op;
        }
        else if (binary.Left is Literal mirrored && binary.Right is ColumnRef right)
        {
            column = def.IndexOf(right.Name);
            if (mirrored.Value is not long number) return false;
            threshold = number;
            text = binary.Op switch
            {
                "<" => ">",
                "<=" => ">=",
                ">" => "<",
                ">=" => "<=",
                _ => binary.Op
            };
        }
        else
        {
            return false;
        }

        if (column < 0 || def.Columns[column].Type != ColumnType.Int) return false;

        switch (text)
        {
            case "=": op = CompareOp.Equal; return true;
            case "<>": op = CompareOp.NotEqual; return true;
            case "<": op = CompareOp.Less; return true;
            case "<=": op = CompareOp.LessOrEqual; return true;
            case ">": op = CompareOp.Greater; return true;
            case ">=": op = CompareOp.GreaterOrEqual; return true;
            default: return false;
        }
    }

    /// <summary>Walks the B+Tree leaf chain, so rows come out ordered by key.</summary>
    private IEnumerable<DataChunk> IndexRangeChunks(Table indexed, long low, long high, Transaction txn)
    {
        var chunk = new DataChunk(indexed.Def.Columns.Count);
        chunk.Reset();

        foreach ((long key, long tupleId) in indexed.PrimaryKeyIndex.Range(low, high))
        {
            RowVersion? version = null;

            if (indexed.Rows.TryGetValue(tupleId, out RowVersion? candidate) && _store.IsVisible(candidate, txn))
                version = candidate;
            else
                version = VisibleVersion(indexed.Def, tupleId, txn);

            if (version is null) continue;

            // The entry may be stale: the row it points at can have been re-keyed.
            if (version.Values[indexed.PrimaryKeyColumn] is not long current || current != key) continue;

            chunk.Append(tupleId, version.Values, indexed.PrimaryKeyColumn);
            if (!chunk.IsFull) continue;

            chunk.Selection.SelectAll(chunk.Count);
            yield return chunk;
            chunk.Reset();
        }

        if (chunk.Count == 0) yield break;

        chunk.Selection.SelectAll(chunk.Count);
        yield return chunk;
    }

    private IEnumerable<DataChunk> VectorChunks(TableDef def, int column, CompareOp op, long threshold, Transaction txn)
    {
        var chunk = new DataChunk(def.Columns.Count);
        chunk.Reset();

        foreach (RowVersion version in _store.Scan(def.Name, txn))
        {
            chunk.Append(version.RowId, version.Values, column);
            if (!chunk.IsFull) continue;

            VectorFilter.Apply(chunk, op, threshold);
            yield return chunk;
            chunk.Reset();
        }

        if (chunk.Count == 0) yield break;

        VectorFilter.Apply(chunk, op, threshold);
        yield return chunk;
    }

    private IEnumerable<DataChunk> ScalarChunks(TableDef def, Expr? where, Transaction txn)
    {
        var chunk = new DataChunk(def.Columns.Count);
        chunk.Reset();

        foreach (RowVersion version in _store.Scan(def.Name, txn))
        {
            chunk.Append(version.RowId, version.Values, -1);
            if (!chunk.IsFull) continue;

            SelectScalar(chunk, def, where);
            yield return chunk;
            chunk.Reset();
        }

        if (chunk.Count == 0) yield break;

        SelectScalar(chunk, def, where);
        yield return chunk;
    }

    private static void SelectScalar(DataChunk chunk, TableDef def, Expr? where)
    {
        chunk.Selection.Clear();

        if (where is null)
        {
            chunk.Selection.SelectAll(chunk.Count);
            return;
        }

        for (int i = 0; i < chunk.Count; i++)
            if (Evaluator.Truthy(Evaluator.Eval(where, def, chunk.Rows[i])))
                chunk.Selection.Add(i);
    }

    /// <summary>Fallback for rows whose newest version is invisible to this snapshot.</summary>
    private RowVersion? VisibleVersion(TableDef def, long rowId, Transaction txn)
    {
        List<RowVersion> versions = _store.GetTable(def.Name).Versions;

        for (int i = versions.Count - 1; i >= 0; i--)
        {
            RowVersion version = versions[i];
            if (version.RowId == rowId && _store.IsVisible(version, txn)) return version;
        }

        return null;
    }

    // ---------------------------------------------------------------- shared

    private static object? Coerce(Column column, object? value)
    {
        if (value is null) return null;

        return column.Type switch
        {
            ColumnType.Int => value is long
                ? value
                : throw new SqlException($"column '{column.Name}' expects INT"),
            ColumnType.Text => value is string
                ? value
                : throw new SqlException($"column '{column.Name}' expects TEXT"),
            ColumnType.Bool => value is bool
                ? value
                : throw new SqlException($"column '{column.Name}' expects BOOL"),
            _ => value
        };
    }

    /// <summary>Publishes the newest version of a row into the primary index.</summary>
    private void IndexRow(TableDef def, long rowId, object?[] values)
    {
        if (_indexes is null) return;
        if (!_indexes.TryGetTable(def.Name, out Table _)) return;

        List<RowVersion> versions = _store.GetTable(def.Name).Versions;
        for (int i = versions.Count - 1; i >= 0; i--)
        {
            if (versions[i].RowId != rowId) continue;
            _indexes.OnRowWritten(def, rowId, versions[i], values);
            return;
        }
    }

    private void CheckPrimaryKey(TableDef def, object?[] values, Transaction txn, long excludedRowId)
    {
        for (int i = 0; i < def.Columns.Count; i++)
        {
            if (!def.Columns[i].PrimaryKey) continue;

            object? candidate = values[i];
            if (candidate is null)
                throw new SqlException($"primary key '{def.Columns[i].Name}' cannot be null");

            // Indexed path: one descent of the B+Tree plus one visibility test.
            if (_indexes is not null
                && candidate is long key
                && _indexes.TryGetTable(def.Name, out Table indexed)
                && indexed.HasPrimaryKey
                && indexed.PrimaryKeyColumn == i)
            {
                if (_indexes.HasVisibleDuplicate(indexed, key, excludedRowId, _store, txn))
                    throw new SqlException($"duplicate primary key in '{def.Name}'");
                continue;
            }

            foreach (RowVersion version in _store.Scan(def.Name, txn))
            {
                if (version.RowId == excludedRowId) continue;
                object? existing = version.Values[i];
                if (existing is not null && Evaluator.Compare(existing, candidate) == 0)
                    throw new SqlException($"duplicate primary key in '{def.Name}'");
            }
        }
    }

    private void CheckForeignKeys(TableDef def, object?[] values, Transaction txn)
    {
        foreach (ForeignKey fk in def.ForeignKeys)
        {
            int index = def.IndexOf(fk.Column);
            if (index < 0) throw new SqlException($"unknown foreign key column '{fk.Column}'");

            object? value = values[index];
            if (value is null) continue;

            TableDef parent = _store.GetTable(fk.ReferencedTable).Def;
            int parentIndex = parent.IndexOf(fk.ReferencedColumn);
            if (parentIndex < 0)
                throw new SqlException($"unknown referenced column '{fk.ReferencedColumn}'");

            // Indexed path when the referenced column is the parent's INT64 primary key:
            // one probe, not a Search followed by a duplicate check.
            if (_indexes is not null
                && value is long key
                && _indexes.TryGetTable(parent.Name, out Table indexedParent)
                && indexedParent.HasPrimaryKey
                && indexedParent.PrimaryKeyColumn == parentIndex)
            {
                if (!_indexes.HasVisibleKey(indexedParent, key, _store, txn))
                    throw new SqlException($"foreign key violation on '{def.Name}.{fk.Column}'");
                continue;
            }

            bool found = false;
            foreach (RowVersion version in _store.Scan(parent.Name, txn))
            {
                object? existing = version.Values[parentIndex];
                if (existing is not null && Evaluator.Compare(existing, value) == 0)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                throw new SqlException($"foreign key violation on '{def.Name}.{fk.Column}'");
        }
    }
}
