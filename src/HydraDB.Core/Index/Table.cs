using HydraDB.Core.Catalog;
using HydraDB.Core.Txn;

namespace HydraDB.Core.Index;

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
    public int PrimaryKeyColumn { get; }
    public BPlusTree PrimaryKeyIndex { get; }
    public Dictionary<long, RowVersion> Rows { get; } = new();
}

/// <summary>
/// Owns one B+Tree per table with an INT64 primary key. The index is a derived
/// structure: it maps key to row id, and MVCC visibility is resolved on the single
/// candidate row instead of scanning the table.
/// </summary>
public sealed class TableIndexSet : IDisposable
{
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

        if (primaryKeyColumn < 0) return;

        string stem = Path.Combine(_directory, "pk." + Sanitize(def.Name));
        _tables[def.Name] = new Table(def, primaryKeyColumn, new BPlusTree(stem + ".idx", stem + ".idxlog"));
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
            table.PrimaryKeyIndex.Truncate();
            table.Rows.Clear();
        }

        foreach ((TableData data, RowVersion version) in store.LiveRows())
        {
            if (!_tables.TryGetValue(data.Def.Name, out Table? table)) continue;

            table.Rows[version.RowId] = version;
            if (version.Values[table.PrimaryKeyColumn] is long key)
                table.PrimaryKeyIndex.Upsert(key, version.RowId);
        }
    }

    public void OnRowWritten(TableDef def, long rowId, RowVersion version, object?[] values)
    {
        if (!_tables.TryGetValue(def.Name, out Table? table)) return;

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
        long? candidate = table.PrimaryKeyIndex.Search(key);
        if (candidate is not long rowId || rowId == excludedRowId) return false;
        if (!table.Rows.TryGetValue(rowId, out RowVersion? version)) return false;
        if (!store.IsVisible(version, txn)) return false;

        return version.Values[table.PrimaryKeyColumn] is long current && current == key;
    }

    public void Checkpoint()
    {
        foreach (Table table in _tables.Values) table.PrimaryKeyIndex.Checkpoint();
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
        foreach (Table table in _tables.Values) table.PrimaryKeyIndex.Dispose();
        _tables.Clear();
    }
}
