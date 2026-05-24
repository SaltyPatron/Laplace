namespace Laplace.SubstrateCRUD;

/// <summary>
/// Result of <see cref="ISubstrateWriter.ApplyAsync"/> — per-intent
/// observability data emitted to <c>laplace_crud_*</c> Prometheus metrics
/// at the orchestration layer (IngestRunner per ADR 0052).
/// </summary>
public sealed record ApplyResult(
    /// <summary>Total entity rows the intent presented (pre-dedup).</summary>
    int EntitiesAttempted,
    /// <summary>Entity rows actually inserted (post-dedup; novel only).</summary>
    int EntitiesInserted,
    int PhysicalitiesAttempted,
    int PhysicalitiesInserted,
    int AttestationsAttempted,
    int AttestationsInserted,
    /// <summary>Number of PG round-trips. Best case (full duplicate)
    /// = 1; novel intent = 4 (one existence SRF + three COPYs).</summary>
    int RoundTrips,
    /// <summary>Total wall-clock spent inside ApplyAsync.</summary>
    TimeSpan WallClock,
    /// <summary>True if this intent was fully a no-op via the trunk-shortcircuit
    /// shortcut (root already in substrate); false otherwise.</summary>
    bool TrunkShortcircuitHit);
