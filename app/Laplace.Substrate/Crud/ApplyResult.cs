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
    bool JournalReplayHit = false);

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
