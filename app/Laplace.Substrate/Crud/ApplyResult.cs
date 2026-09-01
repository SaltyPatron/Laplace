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
    /// <summary>True iff the working set's evidence flush-journal token was
    /// already claimed by a prior committed apply. This proves the additive
    /// evidence write is durable; it deliberately says nothing about whether
    /// the dependent consensus/mask continuation completed. Fold completion is
    /// tracked separately by laplace.ingest_flush_journal.folded.</summary>
    bool JournalReplayHit = false);
