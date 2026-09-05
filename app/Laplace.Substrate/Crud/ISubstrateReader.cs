using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;



public readonly record struct CircuitRelation(
    Hash128 Subject, Hash128 Object, Hash128 TypeId, double EffMu, long Witnesses);

/// <summary>
/// A keyset page of already-adjudicated token relations.  The page boundary is
/// transport only: callers resume with the final (subject, object) key until
/// the complete selected vocabulary has been examined.
/// </summary>
public readonly record struct CircuitCandidatePage(
    IReadOnlyList<CircuitRelation> Rows,
    Hash128? NextSubject,
    Hash128? NextObject);

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

/// <summary>One relation crowding the consensus/attestations DEFAULT partition — a
/// relation carrying real traffic that the manifest never flagged <c>hot = true</c>.</summary>
public readonly record struct PartitionPressure(string Relation, long Rows, double PctOfDefault);

public interface ISubstrateReader
{
    Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default);

    Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default);

    /// <summary>A per-file completion belongs to both the file identity and the
    /// decomposer vendor. The legacy source-only marker cannot distinguish two vendor
    /// implementations that consume identical bytes at the same layer.</summary>
    Task<bool> HasFileCompletedAsync(
        Hash128 fileId, Hash128 decomposerSourceId, int layerOrder,
        CancellationToken ct = default) =>
        HasSourceCompletedAsync(fileId, layerOrder, ct);

    /// <summary>
    /// Batched form of <see cref="HasSourceCompletedAsync"/>: returns the subset of
    /// <paramref name="sourceIds"/> that already carry the layer's completion marker.
    ///
    /// Per-file resume (#898) is ON BY DEFAULT for every <c>DecomposerMultiFile</c>, and
    /// the scalar form is called once per file inside the worker loop. MEASURED on the
    /// 2026-08-10 knowledge seed: FrameNet spent 561s to deposit 1,042,471 rows across
    /// 14,900 files -- 1,857 rows/s against OMW's 22,462 on the same run, and 37.7 ms per
    /// file x 14,900 files = 562s, i.e. essentially the ENTIRE runtime was per-file
    /// overhead rather than payload. The scalar probe is one round trip per file, and the
    /// SQL behind it (<c>ops.evidence_count(...) &gt; 0</c>) counts rows to answer an
    /// existence question.
    ///
    /// This is the shape the write path already rejected everywhere else: the substrate
    /// exposes array-in C primitives (<c>entities_exist_bitmap</c>,
    /// <c>physicalities_exist_bitmap</c>, <c>tier_batch_existence_probe</c>) precisely so
    /// membership questions cost one round trip, not N. Resume was added as a scalar and
    /// never got the same treatment.
    ///
    /// The default implementation loops the scalar form so every existing reader keeps
    /// working unchanged; a store that can answer it in one round trip overrides it.
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

    Task<IReadOnlySet<Hash128>> HasFilesCompletedAsync(
        IReadOnlyList<Hash128> fileIds, Hash128 decomposerSourceId, int layerOrder,
        CancellationToken ct = default) =>
        HasSourcesCompletedAsync(fileIds, layerOrder, ct);

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

    /// <summary>
    /// Returns existing consensus cells of one relation whose two endpoints are
    /// both in <paramref name="vocabulary"/>.  This is the Phase-5a admission
    /// boundary: a checkpoint may evaluate these claims, but cannot manufacture
    /// an unbounded vocabulary-square candidate set.  <paramref name="pageSize"/>
    /// bounds one database transfer only; it never selects a ranked prefix.
    /// </summary>
    Task<CircuitCandidatePage> ReadCircuitCandidatesAsync(
        IReadOnlyList<Hash128> vocabulary, Hash128 typeId,
        Hash128? afterSubject, Hash128? afterObject, int pageSize,
        CancellationToken ct = default)
        => Task.FromResult(new CircuitCandidatePage(Array.Empty<CircuitRelation>(), null, null));

    /// <summary>
    /// OP3 nomination for Phase 5b. It scans existing graph cells whose two
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

    /// <summary>
    /// Relations crowding the DEFAULT partition of consensus, worst first. The hot roster in
    /// <c>engine/manifest/relation_types.toml</c> is a human judgement about traffic, and it goes
    /// stale in silence: a decomposer can become the single largest writer in the database with
    /// every one of its rows piling into one shared heap and btree, and nothing says so. Reported
    /// at the end of every ingest run so the source that causes it is the source that names it.
    /// Defaults to empty for readers/installs without the diagnostic.
    /// </summary>
    Task<IReadOnlyList<PartitionPressure>> PartitionPressureAsync(
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PartitionPressure>>(Array.Empty<PartitionPressure>());
}
