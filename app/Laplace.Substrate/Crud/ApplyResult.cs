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
