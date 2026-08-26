using HydraDB.Core.Catalog;
using HydraDB.Core.Storage;
using HydraDB.Core.Txn;

namespace HydraDB.Core.Index;

/// <summary>One non-primary column plus its own B+Tree. Registered, never auto-created yet.</summary>
public sealed class Secondary
{
    public Secondary(string column, BPlusTree tree)
    {
        Column = column;
        Tree = tree;
    }

    public string Column { get; }
    public BPlusTree Tree { get; }
}

/// <summary>Shape of one primary index at a point in time. Diagnostics only.</summary>
public sealed record IndexStats(
    string Table,
    int Order,
    int Height,
    long KeyCount,
    int PageCount,
    double FillFactor,
    long LogBytes,
    bool CrcOk,
    int SecondaryCount);

/// <summary>A table plus its primary-key index and a row-id to newest-version map.</summary>
public sealed class Table
{
    public Table(TableDef def, int primaryKeyColumn, BPlusTree primaryKeyIndex)
    {
        Def = def;
        PrimaryKeyColumn = primaryKeyColumn;
        PrimaryKeyIndex = primaryKeyIndex;
    }

    public TableDef Def { get; }

    /// <summary>Column index of the INT64 primary key, or the sentinel -1 when the table has none.</summary>
    public int PrimaryKeyColumn { get; }

    /// <summary>True when <see cref="PrimaryKeyColumn"/> is a real column index rather than the -1 sentinel.</summary>
    public bool HasPrimaryKey => PrimaryKeyColumn >= 0;

    /// <summary>Always present. A table without an INT64 primary key owns an empty tree.</summary>
    public BPlusTree PrimaryKeyIndex { get; }

    /// <summary>Secondary indexes by column name. Empty until secondary indexes are wired up.</summary>
    public Dictionary<string, Secondary> Secondary { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<long, RowVersion> Rows { get; } = new();
}

/// <summary>
/// Owns one B+Tree per table. For a table with an INT64 primary key the tree is a derived
/// structure: it maps key to row id, and MVCC visibility is resolved on the single candidate
/// row instead of scanning the table. Tables without such a key are still registered, with
/// the -1 sentinel and an empty tree, so callers can look them up unconditionally.
/// </summary>
public sealed class TableIndexSet : IDisposable
{
    public const string FilePrefix = "pk.";
    public const string PageFileSuffix = ".idx";
    public const string LogFileSuffix = ".idxlog";

    /// <summary>Pre-1.0 log name. Never created any more, only deleted.</summary>
    public const string LegacySuffix = ".index.wal";

    private readonly string _directory;
    private readonly Dictionary<string, Table> _tables = new(StringComparer.OrdinalIgnoreCase);

    public TableIndexSet(string directory) => _directory = directory;

    public bool TryGetTable(string name, out Table table) => _tables.TryGetValue(name, out table!);

    public void Register(TableDef def)
    {
        if (_tables.ContainsKey(def.Name)) return;

        int primaryKeyColumn = -1;
        for (int i = 0; i < def.Columns.Count; i++)
        {
            if (def.Columns[i].PrimaryKey && def.Columns[i].Type == ColumnType.Int)
            {
                primaryKeyColumn = i;
                break;
            }
        }

        string stem = Path.Combine(_directory, FilePrefix + Sanitize(def.Name));
        DeleteLegacyLog(stem);

        try
        {
            _tables[def.Name] = new Table(def, primaryKeyColumn,
                new BPlusTree(stem + PageFileSuffix, stem + LogFileSuffix));
        }
        catch (IOException)
        {
            _tables.Remove(def.Name);
        }
        catch (UnauthorizedAccessException)
        {
            _tables.Remove(def.Name);
        }
    }

    public void RegisterAll(MvccStore store)
    {
        foreach (TableData data in store.Tables.Values) Register(data.Def);
    }

    /// <summary>Rebuilds every index from committed-live rows. Used at open, where the index is authoritative only after the heap is recovered.</summary>
    public void RebuildAll(MvccStore store)
    {
        foreach (Table table in _tables.Values)
        {
            table.Rows.Clear();
            if (!table.HasPrimaryKey) continue;
            table.PrimaryKeyIndex.Truncate();
        }

        foreach ((TableData data, RowVersion version) in store.LiveRows())
        {
            if (!_tables.TryGetValue(data.Def.Name, out Table? table)) continue;
            if (!table.HasPrimaryKey) continue;

            table.Rows[version.RowId] = version;
            if (version.Values[table.PrimaryKeyColumn] is long key)
                table.PrimaryKeyIndex.Upsert(key, version.RowId);
        }
    }

    public void OnRowWritten(TableDef def, long rowId, RowVersion version, object?[] values)
    {
        if (!_tables.TryGetValue(def.Name, out Table? table)) return;
        if (!table.HasPrimaryKey) return;

        table.Rows[rowId] = version;
        if (values[table.PrimaryKeyColumn] is long key)
            table.PrimaryKeyIndex.Upsert(key, rowId);
    }

    /// <summary>
    /// O(log_m n) uniqueness check. An index entry is only a violation when the row it
    /// points at is visible to this snapshot and still carries that key, so entries left
    /// behind by deleted, aborted, or re-keyed rows are treated as free.
    /// </summary>
    public bool HasVisibleDuplicate(Table table, long key, long excludedRowId, MvccStore store, Transaction txn)
    {
        if (!table.HasPrimaryKey) return false;

        long? candidate = table.PrimaryKeyIndex.Search(key);
        if (candidate is not long rowId || rowId == excludedRowId) return false;
        if (!table.Rows.TryGetValue(rowId, out RowVersion? version)) return false;
        if (!store.IsVisible(version, txn)) return false;

        return version.Values[table.PrimaryKeyColumn] is long current && current == key;
    }

    /// <summary>
    /// Existence probe for a key, without excluding any row. Row ids start at 1, so the 0
    /// sentinel can never match a real row and nothing is filtered out.
    /// </summary>
    public bool HasVisibleKey(Table table, long key, MvccStore store, Transaction txn) =>
        HasVisibleDuplicate(table, key, 0, store, txn);

    /// <summary>
    /// Shape of one index. <c>CrcOk</c> is the result of a full structural validation, so this
    /// is a diagnostic call, not something to run per statement. Fill factor is payload bytes
    /// (16 per key) over allocated bytes, so a freshly split tree sits near 50 percent.
    /// </summary>
    public IndexStats? GetStats(string name)
    {
        if (!_tables.TryGetValue(name, out Table? table)) return null;

        BPlusTree tree = table.PrimaryKeyIndex;

        bool crcOk = true;
        try
        {
            tree.Validate();
        }
        catch (InvalidDataException)
        {
            crcOk = false;
        }

        double fillFactor = tree.PageCount > 0
            ? tree.KeyCount * 16.0 / (tree.PageCount * (double)Pager.PageSize) * 100.0
            : 0.0;

        return new IndexStats(
            table.Def.Name,
            tree.Order,
            tree.Height,
            tree.KeyCount,
            tree.PageCount,
            fillFactor,
            tree.LogBytes,
            crcOk,
            table.Secondary.Count);
    }

    public Dictionary<string, IndexStats> GetAllStats()
    {
        var stats = new Dictionary<string, IndexStats>(StringComparer.OrdinalIgnoreCase);

        foreach (Table table in _tables.Values)
            if (GetStats(table.Def.Name) is IndexStats snapshot)
                stats[table.Def.Name] = snapshot;

        return stats;
    }

    public void Checkpoint()
    {
        foreach (Table table in _tables.Values)
        {
            foreach (Secondary secondary in table.Secondary.Values)
                if (!ReferenceEquals(secondary.Tree, table.PrimaryKeyIndex))
                    secondary.Tree.Checkpoint();

            table.PrimaryKeyIndex.Checkpoint();
        }
    }

    private static void DeleteLegacyLog(string stem)
    {
        string legacy = stem + LegacySuffix;

        try
        {
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch (IOException)
        {
            // A stale legacy log is harmless: it is never read.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Sanitize(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return builder.ToString();
    }

    public void Dispose()
    {
        foreach (Table table in _tables.Values)
        {
            foreach (Secondary secondary in table.Secondary.Values)
                if (!ReferenceEquals(secondary.Tree, table.PrimaryKeyIndex))
                    secondary.Tree.Dispose();

            table.PrimaryKeyIndex.Dispose();
        }

        _tables.Clear();
    }
}
