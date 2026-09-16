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
    /// claimed by a prior committed apply — the whole batch (evidence AND
    /// any dependent fold) already landed; every layer must treat the
    /// replay as a no-op.</summary>
    bool JournalReplayHit = false)
{
    /// <summary>Actual PostgreSQL transaction settings and acknowledgement for this apply.
    /// Null means the writer did not establish this PostgreSQL-specific contract.</summary>
    public PostgresCommitReceipt? PostgresCommit { get; init; }
    public PhysicalityAdmissionReceipt? PhysicalityAdmission { get; init; }
    /// <summary>Actual transactions containing COPY, counted once per transaction.
    /// These are not estimates of physical network round trips.</summary>
    public int CopyTransactionsStarted { get; init; }
    public int CopyTransactionsCommitted { get; init; }
}

public sealed record PhysicalityAdmissionReceipt(
    Hash128 FloorReceipt, Hash128 GeneratedSourceId, string SnapshotReceipt, long SourceForms,
    long CurrentContentBodies, long MissingContentBodies, int ProviderRounds,
    int DatabaseOperations, long ReservedPeakBytes, long TupleBytes,
    long FloorIndexAddedBytes, long LogicalWork, long StoredVertices)
{
    public long ClientPayloadGrantBytes { get; init; }
    public long SqlPayloadGrantBytes { get; init; }
    public long LogicalWorkGrant { get; init; }
    public int DatabaseOperationGrant { get; init; }
    public int GeneratedEntityRows { get; init; }
    public int GeneratedPhysicalityRows { get; init; }
    public int GeneratedAttestationRows { get; init; }
    /// <summary>Source-order immutable descriptors and their explicitly selected view disposition.</summary>
    public ImmutableArray<PhysicalityFormReceipt> Forms { get; init; } = [];
    /// <summary>Exact missing entity IDs, addressed by each form's first/count slice.
    /// Each nonempty slice is sorted by canonical ID bytes with no duplicates.</summary>
    public ImmutableArray<Hash128> MissingViewReferences { get; init; } = [];
}

public enum PhysicalityViewState : short
{
    Available = 0,
    MissingReference = 1,
}

/// <summary>A retained descriptor is independent of its optional selected geometry view.
/// MissingFirst and MissingCount index the enclosing receipt's MissingViewReferences.</summary>
public readonly record struct PhysicalityFormReceipt(
    Hash128 DescriptorId, Hash128? ViewId, PhysicalityViewState ViewState,
    long MissingFirst, long MissingCount);

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

public class LegacyReplayRequiresReconciliationException : InvalidOperationException
{
    public LegacyReplayRequiresReconciliationException(Hash128 legacyToken)
        : this($"legacy replay token {legacyToken} cannot prove semantic-payload equality; "
             + "source reconciliation is required before this working set can be accepted", legacyToken)
    {
    }

    protected LegacyReplayRequiresReconciliationException(string message, Hash128 legacyToken)
        : base(message)
        => LegacyToken = legacyToken;

    public Hash128 LegacyToken { get; }
}

public sealed class LegacyBootstrapReconciliationException
    : LegacyReplayRequiresReconciliationException
{
    public LegacyBootstrapReconciliationException(Hash128 marker, string reason)
        : base($"legacy bootstrap marker {marker} exists but its durable payload cannot be "
             + $"reconciled: {reason}", marker)
        => Marker = marker;

    public Hash128 Marker { get; }
}
