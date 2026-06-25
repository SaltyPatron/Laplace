using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

// One row of the decoder-ring read: the strongest relation a (subject, object) token pair already
// holds in consensus. Returned by classify_circuit; consumed by the model HeadClassifier.
public readonly record struct CircuitRelation(
    Hash128 Subject, Hash128 Object, Hash128 TypeId, double EffMu, long Witnesses);

public interface ISubstrateReader
{
    Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default);

    Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default);

    Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default);

    Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default);

    // Mark ids as proven-present in the reader's session seen-set (content is immutable ⇒ permanent).
    // The deferred-content path calls this after staging a batch so re-emitted content is never
    // re-probed or re-staged. Default no-op for readers without a seen-set (test doubles).
    void MarkProven(IReadOnlyList<Hash128> ids) { }

    // compose IS the dedup: cache a canonical's natural-unit root so a re-seen canonical skips
    // BuildContentTree entirely and just attests via the cached root. Defaults: no cache (test doubles).
    bool TryGetCachedRoot(Hash128 canonicalKey, out Hash128 rootId) { rootId = default; return false; }
    void CacheRoot(Hash128 canonicalKey, Hash128 rootId) { }

    // Top-down O(tier) containment probe: same present-bitmap contract as EntitiesExistBitmapAsync
    // (bit k set ⟺ candidate k present) but tree-aware — parents[k] = 0-based parent index, <0 = root;
    // a present trunk short-circuits its subtree. Default: fall back to the flat probe (ignores the
    // tree) so test doubles need not implement the descent; the live reader overrides it.
    Task<byte[]> ContentDescentBitmapAsync(
        IReadOnlyList<Hash128> ids, IReadOnlyList<int> parents, CancellationToken ct = default)
        => EntitiesExistBitmapAsync(ids, ct);

    // Decoder ring: classify a batch of circuit token pairs against pre-existing seed knowledge.
    // Each pair is (subject, object); the result is the strongest consensus relation per pair.
    // Default no-op so readers that do not back onto a live substrate (test doubles) need not
    // implement it; the head classifier simply produces no ENCODES meta-attestations in that case.
    Task<IReadOnlyList<CircuitRelation>> ClassifyCircuitAsync(
        IReadOnlyList<(Hash128 Subject, Hash128 Object)> pairs, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CircuitRelation>>(Array.Empty<CircuitRelation>());
}
