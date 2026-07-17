using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;



public readonly record struct CircuitRelation(
    Hash128 Subject, Hash128 Object, Hash128 TypeId, double EffMu, long Witnesses);

public interface ISubstrateReader
{
    Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default);

    Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default);

    Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default);

    Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default);

    /// <summary>
    /// One round of the tier-by-tier, trunk-to-leaf batch existence probe
    /// (see TierTreeDescent.ProbeBatchEmitBitmapsAsync). The caller passes
    /// exactly the candidate ids for one tier -- <paramref name="tier"/> is
    /// that round's tier, shared by every candidate because the descent is
    /// tier-by-tier by construction; the backing store uses it to prune its
    /// LIST(tier) partitions to one index descent per id -- already
    /// filtered to exclude descendants of nodes a previous (higher-tier)
    /// round confirmed present. A bit in the returned bitmap is set iff
    /// that id was positively confirmed present -- this must NEVER default
    /// to "present" for unresolved candidates; presence is only ever
    /// asserted from a real query result. Default implementation delegates
    /// to <see cref="EntitiesExistBitmapAsync"/>, which has the same safe
    /// semantics.
    /// </summary>
    Task<byte[]> TierBatchExistenceProbeAsync(IReadOnlyList<Hash128> ids, short tier, CancellationToken ct = default)
        => EntitiesExistBitmapAsync(ids, ct);

    /// <summary>
    /// True iff <paramref name="id"/> has been confirmed present in the DB
    /// (via a real batch probe result), or is part of this transaction's
    /// guaranteed-to-be-committed write set. This is NOT "has this id been
    /// seen/probed before" -- an id a probe round positively determined was
    /// ABSENT must never be marked proven. Backing this with a
    /// process-lifetime cache (e.g. NpgsqlSubstrateReader's `_proven`) must
    /// only ever populate it via <see cref="MarkProven"/> calls filtered by
    /// a real presence result -- never unconditionally over a whole probe
    /// batch. Unconditional marking here was the root cause of a real,
    /// live-reproduced bug: a single call's MarkProven(ids) covering the
    /// WHOLE candidate list (including ids that same call had just proven
    /// absent) permanently poisoned the cache, silently skipping every
    /// later occurrence of that content anywhere in the ingest run from
    /// emission (the dorian.txt repro).
    /// </summary>
    bool IsProvenPresent(Hash128 id) => false;

    /// <summary>
    /// Records ids positively confirmed present (see
    /// <see cref="IsProvenPresent"/>). Callers MUST filter to only the
    /// subset of a probe round's candidates that round's own bitmap
    /// actually confirmed present -- never the round's whole candidate
    /// list.
    /// </summary>
    void MarkProven(IReadOnlyList<Hash128> ids) { }



    bool TryGetCachedRoot(Hash128 canonicalKey, out Hash128 rootId) { rootId = default; return false; }
    void CacheRoot(Hash128 canonicalKey, Hash128 rootId) { }





    /// <summary>
    /// Legacy/back-compat: a single flat (ids, parents) probe with no
    /// tier-by-tier short-circuiting. Prefer
    /// <see cref="TierBatchExistenceProbeAsync"/> driven round-by-round by
    /// TierTreeDescent, which is the real replacement for this. `parents`
    /// is accepted for source compatibility with existing callers but is
    /// not used to do any tree-walk here -- this default just delegates to
    /// a flat existence check, which has always been safe (no
    /// default-present assumption).
    /// </summary>
    Task<byte[]> ContentDescentBitmapAsync(
        IReadOnlyList<Hash128> ids, IReadOnlyList<int> parents, CancellationToken ct = default)
        => EntitiesExistBitmapAsync(ids, ct);





    Task<IReadOnlyList<CircuitRelation>> ClassifyCircuitAsync(
        IReadOnlyList<(Hash128 Subject, Hash128 Object)> pairs, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CircuitRelation>>(Array.Empty<CircuitRelation>());






    Task<IReadOnlyList<double>> GetEdgeStrengthsAsync(
        IReadOnlyList<(Hash128 Subject, Hash128 Object)> pairs, Hash128 typeId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<double>>(Array.Empty<double>());
}
