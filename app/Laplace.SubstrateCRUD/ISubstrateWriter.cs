using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// The single substrate write surface every decomposer routes through —
////R6/R16. There is exactly one implementation
/// (<see cref="Npgsql.NpgsqlSubstrateWriter"/>); per-source bespoke insert
/// code is forbidden.
/// </summary>
public interface ISubstrateWriter
{
    /// <summary>Apply one <see cref="SubstrateChange"/> intent. Idempotent on
    /// repeat application of the same intent (ON CONFLICT DO NOTHING per RULES
    /// R5). Race-tolerant under concurrent writers of overlapping intents.
    /// </summary>
    Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default);

    /// <summary>
    /// Apply many intents as ONE batch: a single existence pass and a single
    /// COPY per table across all <paramref name="changes"/>, in one transaction
    /// on one connection. This is the throughput path for mechanical, bulk
    /// sources (model weight tensors, large corpora) — it collapses the ~6
    /// round-trips and 3 connection-opens that <see cref="ApplyAsync"/> pays
    /// PER intent down to ~6 round-trips and 1 connection-open for the whole
    /// batch. Same idempotency / race-tolerance guarantees as
    /// <see cref="ApplyAsync"/>; cross-intent duplicate rows within the batch
    /// are deduped before COPY. The returned <see cref="ApplyResult"/>
    /// aggregates the batch (counts summed, round-trips summed).
    /// </summary>
    async Task<ApplyResult> ApplyManyAsync(IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
    {
        // Correctness-preserving default for writers that don't implement a
        // bulk path (in-memory fakes, test doubles): apply each intent and sum
        // the results. The production NpgsqlSubstrateWriter overrides this with
        // a single batched COPY.
        ArgumentNullException.ThrowIfNull(changes);
        int ea = 0, ei = 0, pa = 0, pi = 0, aa = 0, ai = 0, rt = 0;
        var wall = TimeSpan.Zero;
        bool allShort = changes.Count > 0;
        foreach (var change in changes)
        {
            var r = await ApplyAsync(change, ct);
            ea += r.EntitiesAttempted;      ei += r.EntitiesInserted;
            pa += r.PhysicalitiesAttempted; pi += r.PhysicalitiesInserted;
            aa += r.AttestationsAttempted;  ai += r.AttestationsInserted;
            rt += r.RoundTrips;             wall += r.WallClock;
            allShort &= r.TrunkShortcircuitHit;
        }
        return new ApplyResult(ea, ei, pa, pi, aa, ai, rt, wall, allShort);
    }
}
