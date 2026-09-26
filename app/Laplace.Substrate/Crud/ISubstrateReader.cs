using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>A reader-owned cache lifetime captured before a presence proof or committed write.</summary>
public readonly struct PresenceCacheScope
{
    internal object? State { get; }
    internal PresenceCacheScope(object state) => State = state;
}



public readonly record struct PhysicalityCoverage(
    long GovernedEntities,
    long PlacedEntities)
{
    public long MissingEntities => Math.Max(0, GovernedEntities - PlacedEntities);
    public bool Complete => MissingEntities == 0;
}

public readonly record struct CircuitRelation(
    Hash128 Subject, Hash128 Object, Hash128 TypeId, double EffMu, long Witnesses);

/// <summary>
/// One graph-bounded endpoint pair nominated for model analysis. Basis types
/// prove only why OP3 returned the pair; they are never evidence for the model
/// relation being evaluated.
/// </summary>
public readonly record struct CircuitPairProposal(
    Hash128 Subject, Hash128 Object, IReadOnlyList<Hash128> BasisTypeIds);

public readonly record struct CircuitPairProposalPage(
    IReadOnlyList<CircuitPairProposal> Rows,
    Hash128? NextSubject,
    Hash128? NextObject);


public interface ISubstrateReader
{
    /// <summary>Has any source witness completed this layer (laplace.ingest_layer_completion)?</summary>
    Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default);

    /// <summary>Has this source witness completed this layer? Layer completion is
    /// operational state recorded after a full successful extraction, never testimony.</summary>
    Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default);

    /// <summary>A per-file completion belongs to both the file identity and the
    /// decomposer witness (laplace.ingest_unit_completion): two decomposers consuming
    /// identical bytes at the same layer never share it.</summary>
    Task<bool> HasFileCompletedAsync(
        Hash128 fileId, Hash128 decomposerSourceId, int layerOrder,
        CancellationToken ct = default) =>
        HasSourceCompletedAsync(fileId, layerOrder, ct);

    /// <summary>
    /// Batched form of <see cref="HasSourceCompletedAsync"/>: returns the subset of
    /// <paramref name="sourceIds"/> that have already completed the layer. Resume asks this
    /// once for the whole enumerated file set. The default loops the scalar form; a store
    /// that answers set membership in one round trip, as the array-in existence primitives
    /// do, overrides it.
    /// </summary>
    async Task<IReadOnlySet<Hash128>> HasSourcesCompletedAsync(
        IReadOnlyList<Hash128> sourceIds, int layerOrder, CancellationToken ct = default)
    {
        var done = new HashSet<Hash128>();
        foreach (var id in sourceIds)
            if (await HasSourceCompletedAsync(id, layerOrder, ct).ConfigureAwait(false))
                done.Add(id);
        return done;
    }

    /// <summary>
    /// The file trunks headed by each metadata tree: the entity of the given type whose
    /// trajectory's first constituent is the head. A head with no trunk is absent.
    /// </summary>
    Task<IReadOnlyDictionary<Hash128, OrderedCompositionComponent>> TrunksByHeadAsync(
        IReadOnlyList<Hash128> heads, Hash128 typeId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Hash128, OrderedCompositionComponent>>(
            new Dictionary<Hash128, OrderedCompositionComponent>());

    /// <summary>Admitted entities as composition components: id, tier and coordinate.</summary>
    Task<IReadOnlyDictionary<Hash128, OrderedCompositionComponent>> CompositionComponentsAsync(
        IReadOnlyList<Hash128> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Hash128, OrderedCompositionComponent>>(
            new Dictionary<Hash128, OrderedCompositionComponent>());

    Task<IReadOnlySet<Hash128>> HasFilesCompletedAsync(
        IReadOnlyList<Hash128> fileIds, Hash128 decomposerSourceId, int layerOrder,
        CancellationToken ct = default) =>
        HasSourcesCompletedAsync(fileIds, layerOrder, ct);

    /// <summary>
    /// The subset of <paramref name="keys"/> whose source unit completion is durable
    /// (laplace.ingest_unit_completion, exact witness, unit, layer and digest). A reader
    /// without durable completion state must fail explicitly rather than report a unit
    /// complete or silently re-admit accepted testimony.
    /// </summary>
    Task<IReadOnlySet<IngestUnitCompletionKey>> CompletedUnitsAsync(
        IReadOnlyList<IngestUnitCompletionKey> keys, CancellationToken ct = default)
        => throw new NotSupportedException("reader does not support durable unit completion");

    Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default);

    /// <summary>
    /// Counts every entity one source's testimony touches and how many of them have at least
    /// one durable physicality. Every entity is realized; no source recipe opts out. Readers
    /// without durable physicality storage return (0, 0).
    /// </summary>
    Task<PhysicalityCoverage> PhysicalityCoverageAsync(
        Hash128 sourceId,
        CancellationToken ct = default) =>
        Task.FromResult(new PhysicalityCoverage(0, 0));

    /// <summary>
    /// Coverage restricted to the given relation types: a scoped diagnostic, not the
    /// ingest-completion law.
    /// </summary>
    Task<PhysicalityCoverage> PhysicalityCoverageAsync(
        Hash128 sourceId,
        IReadOnlyList<Hash128> typeIds,
        CancellationToken ct = default) =>
        Task.FromResult(new PhysicalityCoverage(0, 0));

    /// <summary>
    /// Retracts one source's testimony, optionally limited to the given relation and marker
    /// types, when its durable completion marker was written under a different physicality
    /// contract. The default fails explicitly so a reader cannot report stale testimony as
    /// repaired.
    /// </summary>
    Task EvictSourceAsync(
        Hash128 sourceId,
        IReadOnlyList<Hash128>? relationIds,
        IReadOnlyList<Hash128>? markerTypeIds,
        CancellationToken ct = default) =>
        throw new NotSupportedException("reader does not support lawful source eviction");

    Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default);

    /// <summary>
    /// Exact durable testimony in one relation partition. Entity/cache presence is not
    /// acceptance evidence: content COPY can commit before testimony and consensus.
    /// Readers without this capability must fail explicitly rather than report acceptance
    /// or silently treat already accepted testimony as a new recording.
    /// </summary>
    Task<IReadOnlySet<Hash128>> PresentAttestationIdsAsync(
        Hash128 typeId, IReadOnlyList<Hash128> ids, CancellationToken ct = default)
        => throw new NotSupportedException("reader does not support durable attestation presence");

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
    /// True only for confirmed stored presence within this reader's current
    /// cache lifetime. Queued/staged entities are not persisted-presence proof.
    /// False is a cache miss, not a statement that the entity is absent.
    /// </summary>
    bool IsProvenPresent(Hash128 id) => false;

    /// <summary>
    /// Capture before the query or acknowledged write that supplies a positive
    /// presence hint. Readers that cache mutable database presence bind this
    /// scope to their current cache lifetime.
    /// </summary>
    PresenceCacheScope CapturePresenceScope() => default;

    /// <summary>
    /// Promote only positively confirmed IDs using the scope captured before
    /// obtaining that proof. After eviction, stale scopes cannot populate the
    /// replacement cache. Staged-but-uncommitted rows are never valid hints.
    /// </summary>
    void MarkProven(IReadOnlyList<Hash128> ids, PresenceCacheScope scope) => MarkProven(ids);

    /// <summary>
    /// Unscoped hint for readers without a mutable database-presence cache.
    /// A generation-aware reader must not publish IDs from this path.
    /// </summary>
    void MarkProven(IReadOnlyList<Hash128> ids) { }


    bool TryGetCachedRoot(Hash128 canonicalKey, out Hash128 rootId) { rootId = default; return false; }
    void CacheRoot(Hash128 canonicalKey, Hash128 rootId) { }





    /// <summary>
    /// Flat existence probe over <paramref name="ids"/> with no tier-by-tier
    /// short-circuit; <paramref name="parents"/> is not walked. It has the same
    /// never-default-present semantics as <see cref="EntitiesExistBitmapAsync"/>.
    /// The pruned trunk-to-leaf descent is <see cref="TierBatchExistenceProbeAsync"/>
    /// driven round by round by TierTreeDescent.
    /// </summary>
    Task<byte[]> ContentDescentBitmapAsync(
        IReadOnlyList<Hash128> ids, IReadOnlyList<int> parents, CancellationToken ct = default)
        => EntitiesExistBitmapAsync(ids, ct);





    Task<IReadOnlyList<CircuitRelation>> ClassifyCircuitAsync(
        IReadOnlyList<(Hash128 Subject, Hash128 Object)> pairs, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CircuitRelation>>(Array.Empty<CircuitRelation>());

    /// <summary>
    /// OP3 nomination. It scans existing consensus cells whose two
    /// endpoints belong to the selected vocabulary and returns each endpoint
    /// pair once. Existing relation kinds are retained as bounded provenance;
    /// they do not corroborate <paramref name="targetTypeId"/>. A new target
    /// claim still requires governed same-kind model corroboration before OP9.
    /// </summary>
    Task<CircuitPairProposalPage> ReadCircuitPairProposalsAsync(
        IReadOnlyList<Hash128> vocabulary, Hash128 targetTypeId, bool targetSymmetric,
        Hash128? afterSubject, Hash128? afterObject, int pageSize,
        CancellationToken ct = default)
        => Task.FromResult(new CircuitPairProposalPage(
            Array.Empty<CircuitPairProposal>(), null, null));






    Task<IReadOnlyList<double>> GetEdgeStrengthsAsync(
        IReadOnlyList<(Hash128 Subject, Hash128 Object)> pairs, Hash128 typeId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<double>>(Array.Empty<double>());

}
