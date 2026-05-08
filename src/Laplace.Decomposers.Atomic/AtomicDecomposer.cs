namespace Laplace.Decomposers.Atomic;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F4 — ATOMIC 2020 commonsense triple decomposer.
///
/// ATOMIC contributes typed event-to-event / event-to-attribute edges that
/// lexical sources (WordNet, OMW, Wiktionary) and syntactic sources (UD)
/// cannot — causal, temporal, intentional, and effectual structure between
/// events. Per substrate invariant 4 (knowledge IS edges + intersections),
/// these triples become content-addressed binary edges between substrate
/// entities; the same head event observed across multiple commonsense
/// sources collapses to one row, with provenance accumulating per source.
///
/// Decomposer flow per (head, relation, tail) row:
///   1. Skip rows with literal <c>"none"</c> tail (no-answer marker per
///      Hwang 2021).
///   2. F1 TextDecomposer on head + tail surface text → tier-1 entity
///      hashes. Cross-source dedup is automatic ("PersonX goes home" from
///      ATOMIC IS the same entity as "PersonX goes home" from any other
///      source that emits the same surface).
///   3. Resolve relation as a substrate concept entity. ATOMIC's camelCase
///      labels normalize to snake_case for consistency with WordNet
///      pointer concepts.
///   4. Emit binary edge: head → relation_concept → tail.
///   5. Emit provenance edges (entity for head + tail; edge per emitted
///      relation) referencing the <c>atomic_2020</c> source entity.
///
/// Phase 4 / Track F4.
/// </summary>
public sealed class AtomicDecomposer
{
    private static readonly int[] BinaryEdgeRleCounts = new[] { 1, 1, 1 };

    private static readonly Dictionary<string, string> RelationToConcept = new()
    {
        ["AtLocation"]  = "at_location",
        ["CapableOf"]   = "capable_of",
        ["Causes"]      = "causes",
        ["Desires"]     = "desires",
        ["HasProperty"] = "has_property",
        ["HasSubEvent"] = "has_sub_event",
        ["HinderedBy"]  = "hindered_by",
        ["MadeUpOf"]    = "made_up_of",
        ["NotDesires"]  = "not_desires",
        ["ObjectUse"]   = "object_use",
        ["isAfter"]     = "is_after",
        ["isBefore"]    = "is_before",
        ["isFilledBy"]  = "is_filled_by",
        ["oEffect"]     = "o_effect",
        ["oReact"]      = "o_react",
        ["oWant"]       = "o_want",
        ["xAttr"]       = "x_attr",
        ["xEffect"]     = "x_effect",
        ["xIntent"]     = "x_intent",
        ["xNeed"]       = "x_need",
        ["xReact"]      = "x_react",
        ["xReason"]     = "x_reason",
        ["xWant"]       = "x_want",
    };

    private readonly TextDecomposer         _textDecomposer;
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _conceptResolver;
    private readonly IEdgeEmission          _edgeEmission;
    private readonly IProvenance            _provenance;

    public AtomicDecomposer(
        TextDecomposer         textDecomposer,
        IIdentityHashing       hashing,
        IConceptEntityResolver conceptResolver,
        IEdgeEmission          edgeEmission,
        IProvenance            provenance)
    {
        _textDecomposer  = textDecomposer;
        _hashing         = hashing;
        _conceptResolver = conceptResolver;
        _edgeEmission    = edgeEmission;
        _provenance      = provenance;
    }

    /// <summary>
    /// Decompose every triple in one ATOMIC TSV file (train/dev/test).
    /// Idempotent: repeated invocation re-emits the same content-addressed
    /// rows under ON CONFLICT DO NOTHING semantics in the sink.
    /// </summary>
    public async Task DecomposeAsync(
        string             atomicTsvFile,
        CancellationToken  cancellationToken)
    {
        if (!File.Exists(atomicTsvFile)) { return; }

        var sourceHash  = await _provenance.ResolveSourceAsync(
            "atomic_2020", cancellationToken).ConfigureAwait(false);
        var subjectRole = _conceptResolver.Resolve("subject");
        var objectRole  = _conceptResolver.Resolve("object");

        // Cache resolved relation concept entity hashes — all triples share
        // 23 relation kinds; resolver call per row would be wasteful.
        var relationCache = new Dictionary<string, AtomId>();

        foreach (var row in AtomicTsvParser.Parse(atomicTsvFile))
        {
            if (string.Equals(row.Tail, "none", System.StringComparison.Ordinal)) { continue; }
            if (string.IsNullOrEmpty(row.Head) || string.IsNullOrEmpty(row.Tail)) { continue; }
            if (!RelationToConcept.TryGetValue(row.Relation, out var conceptName)) { continue; }

            if (!relationCache.TryGetValue(conceptName, out var relationHash))
            {
                relationHash = _conceptResolver.Resolve(conceptName);
                relationCache[conceptName] = relationHash;
            }

            var headHash = await _textDecomposer.DecomposeAsync(
                row.Head, cancellationToken).ConfigureAwait(false);
            var tailHash = await _textDecomposer.DecomposeAsync(
                row.Tail, cancellationToken).ConfigureAwait(false);

            await _provenance.EmitEntityProvenanceAsync(
                new EntityProvenanceRecord(EntityHash: headHash, SourceHash: sourceHash),
                cancellationToken).ConfigureAwait(false);
            await _provenance.EmitEntityProvenanceAsync(
                new EntityProvenanceRecord(EntityHash: tailHash, SourceHash: sourceHash),
                cancellationToken).ConfigureAwait(false);

            await EmitBinaryEdgeAsync(
                edgeType:    relationHash,
                subjectRole: subjectRole,
                subjectHash: headHash,
                objectRole:  objectRole,
                objectHash:  tailHash,
                sourceHash:  sourceHash,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EmitBinaryEdgeAsync(
        AtomId            edgeType,
        AtomId            subjectRole,
        AtomId            subjectHash,
        AtomId            objectRole,
        AtomId            objectHash,
        AtomId            sourceHash,
        CancellationToken cancellationToken)
    {
        var edgeHash = _hashing.CompositionId(
            new[] { edgeType, subjectHash, objectHash }, BinaryEdgeRleCounts);

        await _edgeEmission.EmitEdgeAsync(
            new EdgeRecord(EdgeTypeHash: edgeType, Hash: edgeHash),
            cancellationToken).ConfigureAwait(false);
        await _edgeEmission.EmitMemberAsync(
            new EdgeMemberRecord(edgeType, edgeHash, subjectRole, 0, subjectHash),
            cancellationToken).ConfigureAwait(false);
        await _edgeEmission.EmitMemberAsync(
            new EdgeMemberRecord(edgeType, edgeHash, objectRole, 0, objectHash),
            cancellationToken).ConfigureAwait(false);
        await _provenance.EmitEdgeProvenanceAsync(
            new EdgeProvenanceRecord(edgeType, edgeHash, sourceHash),
            cancellationToken).ConfigureAwait(false);
    }
}
