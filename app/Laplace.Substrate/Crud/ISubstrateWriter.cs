using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>Run-monotonic counters exposed by writers that fold testimony.</summary>
public interface IConsensusFoldMetrics
{
    long ObservationsAccumulated { get; }
    long CellsFolded { get; }
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
    /// Declares that a bulk ingest run is starting. Writers that cycle
    /// secondary indexes for fresh-seed-shaped applies scope the cycle to
    /// the whole run: drop at the first qualifying apply, keep them down
    /// across every apply of the run, rebuild once at
    /// <see cref="CompleteBulkRunAsync"/>. Rebuild cost is a full-table
    /// scan per index, so per-apply cycling is O(applies × table size) —
    /// the run, not the apply, is the correct scope. No-op by default.
    /// </summary>
    Task BeginBulkRunAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Ends the bulk run declared by <see cref="BeginBulkRunAsync"/>:
    /// rebuilds whatever indexes the run dropped. Must be called on every
    /// exit path (the index-cycle journal makes a missed call recoverable,
    /// not free — reads degrade until the next recovery). No-op by default.
    /// </summary>
    Task CompleteBulkRunAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
