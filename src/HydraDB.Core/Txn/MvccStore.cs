using HydraDB.Core.Catalog;
using HydraDB.Core.Log;

namespace HydraDB.Core.Txn;

public sealed class RowVersion
{
    public long RowId;
    public object?[] Values = Array.Empty<object?>();

    /// <summary>Transaction that created this version.</summary>
    public long Xmin;

    /// <summary>Transaction that deleted this version, or 0 when the version is not deleted.</summary>
    public long Xmax;
}

public sealed class TableData
{
    public TableData(TableDef def) => Def = def;

    public TableDef Def;
    public List<RowVersion> Versions = new();
    public long NextRowId = 1;

    /// <summary>Committed row mutations, used as the heat signal in the graph model.</summary>
    public long Writes;
}

/// <summary>
/// Multi-version store with snapshot isolation and first-updater-wins write conflict
/// detection. Durability is layered on top by <see cref="Engine"/>; this type is pure
/// in-memory state plus visibility arithmetic.
/// </summary>
public sealed class MvccStore
{
    private readonly Dictionary<string, TableData> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, Transaction> _txns = new();
    private long _nextTid = 1;
    private long _commitSeq;

    public IReadOnlyDictionary<string, TableData> Tables => _tables;

    public long CommitSeq => _commitSeq;

    public Transaction BeginTxn()
    {
        var txn = new Transaction(_nextTid++, _commitSeq);
        _txns[txn.Tid] = txn;
        return txn;
    }

    public bool HasTable(string name) => _tables.ContainsKey(name);

    public TableData GetTable(string name)
    {
        if (!_tables.TryGetValue(name, out TableData? table))
            throw new InvalidOperationException($"unknown table '{name}'");
        return table;
    }

    public void CreateTable(TableDef def, Transaction txn)
    {
        if (_tables.ContainsKey(def.Name))
            throw new InvalidOperationException($"table '{def.Name}' already exists");
        _tables[def.Name] = new TableData(def);
        txn.PendingOps.Add(Op.CreateTable(def));
    }

    public long Insert(string table, object?[] values, Transaction txn, long rowId = 0)
    {
        TableData data = GetTable(table);
        long id = rowId != 0 ? rowId : data.NextRowId++;
        if (id >= data.NextRowId) data.NextRowId = id + 1;

        data.Versions.Add(new RowVersion { RowId = id, Values = values, Xmin = txn.Tid, Xmax = 0 });
        txn.PendingOps.Add(Op.Insert(table, id, values));
        return id;
    }

    public IEnumerable<RowVersion> Scan(string table, Transaction txn)
    {
        TableData data = GetTable(table);
        foreach (RowVersion version in data.Versions)
            if (IsVisible(version, txn))
                yield return version;
    }

    public void Delete(string table, RowVersion version, Transaction txn)
    {
        GetTable(table);
        if (HasWriteConflict(version.Xmax, txn))
            throw new SerializationConflictException(
                $"row {version.RowId} in '{table}' was concurrently modified");

        version.Xmax = txn.Tid;
        txn.PendingOps.Add(Op.Delete(table, version.RowId));
    }

    public void Update(string table, RowVersion version, object?[] newValues, Transaction txn)
    {
        Delete(table, version, txn);
        Insert(table, newValues, txn, version.RowId);
    }

    public bool IsVisible(RowVersion version, Transaction txn)
    {
        bool inserted = version.Xmin == txn.Tid || CommittedBeforeSnapshot(version.Xmin, txn);
        if (!inserted) return false;

        bool deleted = version.Xmax != 0 &&
                       (version.Xmax == txn.Tid || CommittedBeforeSnapshot(version.Xmax, txn));
        return !deleted;
    }

    private bool CommittedBeforeSnapshot(long tid, Transaction txn)
    {
        if (tid == 0) return false;
        return _txns.TryGetValue(tid, out Transaction? other)
               && other.State == TxnState.Committed
               && other.CommitSeq <= txn.SnapshotSeq;
    }

    private bool HasWriteConflict(long xmax, Transaction txn)
    {
        if (xmax == 0 || xmax == txn.Tid) return false;
        if (!_txns.TryGetValue(xmax, out Transaction? other)) return true;

        return other.State switch
        {
            TxnState.Active => true,
            TxnState.Committed => other.CommitSeq > txn.SnapshotSeq,
            _ => false
        };
    }

    public List<Op> PrepareCommit(Transaction txn)
    {
        if (txn.State != TxnState.Active)
            throw new InvalidOperationException($"transaction {txn.Tid} is {txn.State}");
        return txn.PendingOps;
    }

    public void MarkCommitted(Transaction txn)
    {
        txn.CommitSeq = ++_commitSeq;
        txn.State = TxnState.Committed;

        foreach (Op op in txn.PendingOps)
            if (op.Kind != OpKind.CreateTable && _tables.TryGetValue(op.Table, out TableData? table))
                table.Writes++;
    }

    public void Abort(Transaction txn)
    {
        if (txn.State != TxnState.Active) return;
        txn.State = TxnState.Aborted;

        foreach (Op op in txn.PendingOps)
            if (op.Kind == OpKind.CreateTable)
                _tables.Remove(op.Table);

        txn.PendingOps.Clear();
    }

    /// <summary>Applies a redo op during snapshot load or WAL replay without re-logging it.</summary>
    public void ApplyReplayOp(Op op, Transaction txn)
    {
        switch (op.Kind)
        {
            case OpKind.CreateTable:
                if (!_tables.ContainsKey(op.Table))
                    _tables[op.Table] = new TableData(op.Def!);
                break;

            case OpKind.Insert:
            {
                TableData table = GetTable(op.Table);
                RowVersion? previous = FindLatest(table, op.RowId);
                if (previous is not null && previous.Xmax == 0) previous.Xmax = txn.Tid;

                table.Versions.Add(new RowVersion
                {
                    RowId = op.RowId,
                    Values = op.Values!,
                    Xmin = txn.Tid,
                    Xmax = 0
                });
                if (op.RowId >= table.NextRowId) table.NextRowId = op.RowId + 1;
                break;
            }

            case OpKind.Delete:
            {
                TableData table = GetTable(op.Table);
                RowVersion? version = FindLatest(table, op.RowId);
                if (version is not null && version.Xmax == 0) version.Xmax = txn.Tid;
                break;
            }
        }
    }

    private static RowVersion? FindLatest(TableData table, long rowId)
    {
        for (int i = table.Versions.Count - 1; i >= 0; i--)
            if (table.Versions[i].RowId == rowId)
                return table.Versions[i];
        return null;
    }

    /// <summary>Rows that are committed and not committed-deleted; the checkpoint image.</summary>
    public IEnumerable<(TableData Table, RowVersion Version)> LiveRows()
    {
        foreach (TableData table in _tables.Values)
            foreach (RowVersion version in table.Versions)
                if (IsCommittedLive(version))
                    yield return (table, version);
    }

    private bool IsCommittedLive(RowVersion version)
    {
        bool inserted = _txns.TryGetValue(version.Xmin, out Transaction? creator)
                        && creator.State == TxnState.Committed;
        if (!inserted) return false;

        bool deleted = version.Xmax != 0
                       && _txns.TryGetValue(version.Xmax, out Transaction? deleter)
                       && deleter.State == TxnState.Committed;
        return !deleted;
    }

    /// <summary>Drops versions that no live snapshot can reach. Returns how many were removed.</summary>
    public int Vacuum()
    {
        long horizon = OldestActiveSnapshot();
        int removed = 0;

        foreach (TableData table in _tables.Values)
        {
            removed += table.Versions.RemoveAll(version =>
                (_txns.TryGetValue(version.Xmin, out Transaction? creator) && creator.State == TxnState.Aborted)
                || (version.Xmax != 0
                    && _txns.TryGetValue(version.Xmax, out Transaction? deleter)
                    && deleter.State == TxnState.Committed
                    && deleter.CommitSeq <= horizon));
        }

        return removed;
    }

    private long OldestActiveSnapshot()
    {
        long oldest = _commitSeq;
        foreach (Transaction txn in _txns.Values)
            if (txn.State == TxnState.Active && txn.SnapshotSeq < oldest)
                oldest = txn.SnapshotSeq;
        return oldest;
    }
}
