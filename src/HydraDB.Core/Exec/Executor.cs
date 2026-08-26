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
/// Executes a parsed statement against a snapshot. Every read goes through
/// <see cref="MvccStore.Scan"/>, so constraint checks observe exactly the rows the
/// statement itself can see. When a B+Tree primary index exists for the table, the
/// uniqueness check is O(log_m n) instead of a linear scan.
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

    private QueryResult Insert(InsertStmt statement, Transaction txn)
    {
        TableDef def = _store.GetTable(statement.Table).Def;
        int inserted = 0;

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

            CheckPrimaryKey(def, values, txn, 0);
            CheckForeignKeys(def, values, txn);

            long rowId = _store.Insert(def.Name, values, txn);
            IndexRow(def, rowId, values);
            inserted++;
        }

        return QueryResult.Changed(inserted);
    }

    private QueryResult Select(SelectStmt statement, Transaction txn)
    {
        TableDef def = _store.GetTable(statement.Table).Def;

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

        LastPlan = ScanPlan.HeapScalar;

        var matched = new List<object?[]>();
        foreach (RowVersion version in _store.Scan(def.Name, txn))
            if (statement.Where is null || Evaluator.Truthy(Evaluator.Eval(statement.Where, def, version.Values)))
                matched.Add(version.Values);

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
