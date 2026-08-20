using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>Run-monotonic counters exposed by writers that fold testimony.</summary>
public interface IConsensusFoldMetrics
{
    long ObservationsAccumulated { get; }
    long CellsFolded { get; }
    TimeSpan LastFoldDrainWallClock => TimeSpan.Zero;
    TimeSpan LastWriterMaintenanceWallClock => TimeSpan.Zero;
    TimeSpan LastFoldSpanWallClock => TimeSpan.Zero;
    TimeSpan ConsensusUpsertBackendWallClock => TimeSpan.Zero;
    TimeSpan HighwayMaskBackendWallClock => TimeSpan.Zero;
    long ConsensusUpsertCalls => 0;
    long HighwayMaskCalls => 0;
    long HighwayMaskPairs => 0;
}

public enum BulkRunCompletionPhase
{
    ConsensusDrain,
    WriterMaintenance,
}

public interface ISubstrateWriter
{
    /// <summary>
    /// Awaits every fold this writer has queued. Default no-op: only the
    /// consensus-accumulating writer defers work past the apply call.
    /// </summary>
    /// <remarks>
    /// On the interface because the RUNNER has to drain before it cancels the run
    /// token. That token is what the fold lanes hold, so cancelling it while a fold
    /// is mid-statement sends a Postgres cancel into it — measured as 57014 inside
    /// consensus.highway_mask_deposit, at the end of a fully successful decompose.
    /// A completed decompose owes its folds a drain; cancellation must mean abnormal
    /// teardown and nothing else.
    /// </remarks>
    Task DrainFoldsAsync() => Task.CompletedTask;

    Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default);

    async Task<ApplyResult> ApplyManyAsync(IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        int ea = 0, ei = 0, pa = 0, pi = 0, aa = 0, ai = 0, rt = 0;
        long es = 0, ps = 0;
        var wall = TimeSpan.Zero;
        bool allShort = changes.Count > 0;
        foreach (var change in changes)
        {
            var r = await ApplyAsync(change, ct);
            ea += r.EntitiesAttempted; ei += r.EntitiesInserted;
            pa += r.PhysicalitiesAttempted; pi += r.PhysicalitiesInserted;
            aa += r.AttestationsAttempted; ai += r.AttestationsInserted;
            rt += r.RoundTrips; wall += r.WallClock;
            es += r.EntitiesSkippedAtMerge; ps += r.PhysicalitiesSkippedAtMerge;
            allShort &= r.TrunkShortcircuitHit;
        }
        return new ApplyResult(ea, ei, pa, pi, aa, ai, rt, wall, allShort, es, ps);
    }

    Task<ApplyResult> AppendAsync(
        IReadOnlyList<SubstrateChange> changes, Hash128 sourceId, CancellationToken ct = default)
        => ApplyManyAsync(changes, ct);

    /// <summary>
    /// Applies one whole working set (Rule #8 step 6: the client dedups and
    /// compose descent proves novelty; the writer apply-side bitmap-probes
    /// claimed-novel ids, bulk-COPYs survivors, and attestation_merge handles
    /// present rows). Implementations without a working-set lane fall back to a
    /// plain apply.
    /// </summary>
    Task<ApplyResult> ApplyWorkingSetAsync(SubstrateChange change, CancellationToken ct = default)
        => ApplyAsync(change, ct);

    /// <summary>
    /// Applies a group of changes as ONE working set — one transaction, one
    /// verification pass, one idempotency token derived from every member's
    /// intent hash. The runner accumulates per-file/per-budget changes and
    /// closes them here.
    /// </summary>
    Task<ApplyResult> ApplyWorkingSetAsync(IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        => ApplyManyAsync(changes, ct);

    Task<(int Entities, int Physicalities, int Attestations)> FinalizeSourceAsync(
        Hash128 sourceId, CancellationToken ct = default)
        => Task.FromResult((0, 0, 0));

    /// <summary>
    /// Declares that a bulk ingest run is starting. Implementations use this boundary for
    /// run-scoped caches, recovery checks, and other writer state. Production indexes remain
    /// online throughout the run. No-op by default.
    /// </summary>
    Task BeginBulkRunAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Ends the bulk run declared by <see cref="BeginBulkRunAsync"/> and releases run-scoped
    /// writer state. No-op by default.
    /// </summary>
    Task CompleteBulkRunAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Ends a bulk run while reporting the two completion barriers separately.
    /// The default writer has no queued consensus lane, so its only barrier is
    /// writer maintenance. Consensus-accumulating writers override this to
    /// report the fold drain before the inner writer's completion work.
    /// </summary>
    Task CompleteBulkRunAsync(
        Action<BulkRunCompletionPhase>? onPhase,
        CancellationToken ct = default)
    {
        onPhase?.Invoke(BulkRunCompletionPhase.WriterMaintenance);
        return CompleteBulkRunAsync(ct);
    }
}
