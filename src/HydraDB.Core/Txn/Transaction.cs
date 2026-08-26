using HydraDB.Core.Log;

namespace HydraDB.Core.Txn;

public enum TxnState
{
    Active,
    Committed,
    Aborted
}

public sealed class Transaction
{
    internal Transaction(long tid, long snapshotSeq)
    {
        Tid = tid;
        SnapshotSeq = snapshotSeq;
    }

    /// <summary>Monotonic transaction id, written into row versions as Xmin/Xmax.</summary>
    public long Tid { get; }

    /// <summary>Commit sequence number observed when this transaction started.</summary>
    public long SnapshotSeq { get; }

    public TxnState State { get; internal set; } = TxnState.Active;

    internal long CommitSeq { get; set; } = -1;

    internal List<Op> PendingOps { get; } = new();
}

public sealed class SerializationConflictException : Exception
{
    public SerializationConflictException(string message) : base(message) { }
}
