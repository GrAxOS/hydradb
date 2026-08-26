using System.Text;
using HydraDB.Core.Exec;
using HydraDB.Core.Index;
using HydraDB.Core.Log;
using HydraDB.Core.Sql;
using HydraDB.Core.Storage;
using HydraDB.Core.Txn;

namespace HydraDB.Core;

/// <summary>
/// One database directory: hydra.snap (checkpoint image), hydra.wal (redo log), and one
/// pk.&lt;table&gt;.idx / .idxlog pair per table with an INT64 primary key.
///
/// Recovery order at open: load the snapshot image, replay every intact WAL record on top
/// of it, then rebuild the derived primary indexes. Commit order: append one record,
/// fsync, then publish the commit sequence number. Checkpoint order: write and fsync the
/// new image, reset the WAL, checkpoint the indexes, then vacuum.
/// </summary>
public sealed class Engine : IDisposable
{
    private const string SnapshotMagic = "HYDRA1";
    private const string SnapshotFileName = "hydra.snap";
    private const string WalFileName = "hydra.wal";

    private readonly Pager _pager;
    private readonly Wal _wal;
    private readonly MvccStore _store = new();
    private readonly TableIndexSet _indexes;
    private readonly Executor _executor;
    private Transaction? _session;
    private long _commits;

    public Engine(string directory)
    {
        Directory.CreateDirectory(directory);
        _pager = new Pager(Path.Combine(directory, SnapshotFileName));
        _wal = new Wal(Path.Combine(directory, WalFileName));
        _indexes = new TableIndexSet(directory);
        _executor = new Executor(_store, _indexes);

        LoadSnapshot();
        Replay();

        _indexes.RegisterAll(_store);
        _indexes.RebuildAll(_store);
    }

    public MvccStore Store => _store;

    public TableIndexSet Indexes => _indexes;

    public long Commits => _commits;

    public long WalBytes => _wal.SizeBytes;

    public Transaction Begin() => _store.BeginTxn();

    public QueryResult Execute(string sql, Transaction? txn = null)
    {
        Stmt statement = new Parser(sql).ParseStatement();

        if (statement is TransactionStmt control) return RunControl(control);

        Transaction? target = txn ?? _session;
        if (target is not null)
        {
            QueryResult scoped = _executor.Run(statement, target);
            RegisterNewTable(statement);
            return scoped;
        }

        Transaction auto = _store.BeginTxn();
        try
        {
            QueryResult result = _executor.Run(statement, auto);
            Commit(auto);
            RegisterNewTable(statement);
            return result;
        }
        catch
        {
            _store.Abort(auto);
            throw;
        }
    }

    private void RegisterNewTable(Stmt statement)
    {
        if (statement is CreateTableStmt created) _indexes.Register(created.Def);
    }

    private QueryResult RunControl(TransactionStmt control)
    {
        switch (control.Kind)
        {
            case TransactionKind.Begin:
                if (_session is not null)
                    throw new InvalidOperationException($"transaction {_session.Tid} is already open");
                _session = _store.BeginTxn();
                return QueryResult.Message($"BEGIN {_session.Tid}");

            case TransactionKind.Commit:
            {
                Transaction open = _session ?? throw new InvalidOperationException("no open transaction");
                Commit(open);
                _session = null;
                return QueryResult.Message("COMMIT");
            }

            default:
            {
                Transaction open = _session ?? throw new InvalidOperationException("no open transaction");
                Rollback(open);
                _session = null;
                return QueryResult.Message("ROLLBACK");
            }
        }
    }

    public void Commit(Transaction txn)
    {
        List<Op> ops = _store.PrepareCommit(txn);

        if (ops.Count > 0)
        {
            _wal.Append(OpCodec.EncodeCommit(txn.Tid, ops));
            _wal.Sync();
        }

        _store.MarkCommitted(txn);
        _commits++;
    }

    public void Rollback(Transaction txn) => _store.Abort(txn);

    public void Checkpoint()
    {
        WriteSnapshot();
        _wal.Reset();
        _indexes.Checkpoint();
        _store.Vacuum();
    }

    private void Replay()
    {
        foreach (byte[] payload in _wal.Replay())
        {
            List<Op> ops = OpCodec.DecodeCommit(payload);
            Transaction txn = _store.BeginTxn();
            foreach (Op op in ops) _store.ApplyReplayOp(op, txn);
            _store.MarkCommitted(txn);
        }
    }

    private void LoadSnapshot()
    {
        if (_pager.PageCount == 0) return;

        byte[] header = _pager.Read(0);
        if (Encoding.ASCII.GetString(header, 0, SnapshotMagic.Length) != SnapshotMagic)
            throw new InvalidDataException("snapshot file is not a HydraDB image");

        int dataPages = BitConverter.ToInt32(header, 8);
        Transaction txn = _store.BeginTxn();

        for (int page = 1; page <= dataPages; page++)
        {
            var slotted = new SlottedPage(_pager.Read(page));
            for (int slot = 0; slot < slotted.SlotCount; slot++)
            {
                foreach (Op op in OpCodec.DecodeCommit(slotted.Get(slot).ToArray()))
                    _store.ApplyReplayOp(op, txn);
            }
        }

        _store.MarkCommitted(txn);
    }

    private void WriteSnapshot()
    {
        var pages = new List<SlottedPage>();
        var current = new SlottedPage();

        void Emit(byte[] record)
        {
            if (current.TryAdd(record)) return;

            pages.Add(current);
            current = new SlottedPage();
            if (!current.TryAdd(record))
                throw new InvalidOperationException("record does not fit in a single page");
        }

        foreach (TableData table in _store.Tables.Values)
            Emit(EncodeSingle(Op.CreateTable(table.Def)));

        foreach ((TableData table, RowVersion version) in _store.LiveRows())
            Emit(EncodeSingle(Op.Insert(table.Def.Name, version.RowId, version.Values)));

        pages.Add(current);

        _pager.Truncate();

        var header = new byte[Pager.PageSize];
        Encoding.ASCII.GetBytes(SnapshotMagic).CopyTo(header, 0);
        BitConverter.GetBytes(pages.Count).CopyTo(header, 8);
        _pager.Write(0, header);

        for (int i = 0; i < pages.Count; i++) _pager.Write(i + 1, pages[i].Buffer);

        _pager.Flush(true);
    }

    private static byte[] EncodeSingle(Op op) => OpCodec.EncodeCommit(0, new List<Op> { op });

    public void Dispose()
    {
        _indexes.Dispose();
        _wal.Dispose();
        _pager.Dispose();
    }
}
