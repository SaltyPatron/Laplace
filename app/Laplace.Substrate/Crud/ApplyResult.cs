using System.Collections.Immutable;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

public sealed record ApplyResult(
    int EntitiesAttempted,
    int EntitiesInserted,
    int PhysicalitiesAttempted,
    int PhysicalitiesInserted,
    int AttestationsAttempted,
    int AttestationsInserted,
    int RoundTrips,
    TimeSpan WallClock,
    bool TrunkShortcircuitHit,
    long EntitiesSkippedAtMerge = 0,
    long PhysicalitiesSkippedAtMerge = 0,
    /// <summary>True iff the working set's flush-journal token was already
    /// claimed by a prior committed apply.</summary>
    bool JournalReplayHit = false)
{
    /// <summary>Actual PostgreSQL transaction settings and acknowledgement for this apply.
    /// Null means the writer did not establish this PostgreSQL-specific contract.</summary>
    public PostgresCommitReceipt? PostgresCommit { get; init; }
    /// <summary>Actual transactions containing COPY, counted once per transaction.
    /// These are not estimates of physical network round trips.</summary>
    public int CopyTransactionsStarted { get; init; }
    public int CopyTransactionsCommitted { get; init; }
}

public enum PhysicalityViewState : short
{
    Available = 0,
    MissingReference = 1,
}

/// <summary>A canonical typed physicality and its directly available native view.</summary>
public readonly record struct PhysicalityFormReceipt(
    Hash128 PhysicalityId, Hash128? ViewId, PhysicalityViewState ViewState,
    long MissingFirst, long MissingCount)
{
    [Obsolete("Use PhysicalityId; ordinary descriptor entities are no longer generated during ingestion.")]
    public Hash128 DescriptorId => PhysicalityId;
}

/// <summary>Caller-selected acknowledgement policy; neither mode changes the staged rows,
/// the working-set identity, or the evidence/consensus transaction boundary.</summary>
public enum PostgresWriteDurability
{
    Asynchronous,
    Synchronous,
}

public sealed record PostgresCommitReceipt(
    string SynchronousCommit, bool Fsync, bool FullPageWrites, bool WriteCommitAcknowledged)
{
    /// <summary>Local PostgreSQL WAL acknowledgement only; this is not a replication,
    /// backup, filesystem mount or physical-device power-loss certification.</summary>
    public bool LocalWalFlushAcknowledged => WriteCommitAcknowledged
        && SynchronousCommit == "on" && Fsync && FullPageWrites;
}

