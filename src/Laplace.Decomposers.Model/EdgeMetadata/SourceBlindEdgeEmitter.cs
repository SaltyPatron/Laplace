namespace Laplace.Decomposers.Model.EdgeMetadata;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// Centralizes the canonical source-blind edge emission shape that EVERY
/// per-tensor extractor (Attention / FFN / LmHead / Conv / Detection /
/// CrossModal / ASR / TTS / Reranker / MoE / Diffusion) follows.
///
/// Three composition steps:
///   1. <see cref="EmitEdgeAsync"/> — primary edge: edge_type_hash + 2
///      members (source role → A, target role → B). Composition is
///      `[edge_type_hash, A, B]`; type is just kind (NEVER includes
///      model/layer/head — those go in provenance composition).
///   2. <see cref="EmitProvenanceAsync"/> — attestation: provenance entity
///      composing the supplied atoms (typically [model, attestor, ts]) +
///      edge_provenance row linking edge → provenance entity + N from_X
///      meta-edges (one per attestor, e.g., from_model, from_head,
///      from_neuron). Multiple attestors of the SAME (A, B) pair
///      accumulate distinct provenance entities on the SHARED edge — the
///      substrate-invariant source-blind dedup mechanism.
///   3. <see cref="EmitHasMagnitudeAsync"/> — has_magnitude meta-edge
///      from the carrier edge to the F2-decomposed magnitude entity.
///
/// Phase 4 / Track F5 / supports #38 per-tensor extractors.
/// </summary>
public sealed class SourceBlindEdgeEmitter
{
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _concepts;
    private readonly IEdgeEmission          _edges;
    private readonly IProvenance            _provenance;

    private readonly Lazy<AtomId> _sourceRole;
    private readonly Lazy<AtomId> _targetRole;

    private readonly Lazy<AtomId> _hasMagnitudeKind;
    private readonly Lazy<AtomId> _carrierRole;
    private readonly Lazy<AtomId> _valueRole;

    public SourceBlindEdgeEmitter(
        IIdentityHashing       hashing,
        IConceptEntityResolver concepts,
        IEdgeEmission          edges,
        IProvenance            provenance)
    {
        _hashing    = hashing    ?? throw new ArgumentNullException(nameof(hashing));
        _concepts   = concepts   ?? throw new ArgumentNullException(nameof(concepts));
        _edges      = edges      ?? throw new ArgumentNullException(nameof(edges));
        _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));

        _sourceRole = new Lazy<AtomId>(() => _concepts.Resolve("source"));
        _targetRole = new Lazy<AtomId>(() => _concepts.Resolve("target"));

        _hasMagnitudeKind = new Lazy<AtomId>(() => _concepts.Resolve("has_magnitude"));
        _carrierRole      = new Lazy<AtomId>(() => _concepts.Resolve("carrier"));
        _valueRole        = new Lazy<AtomId>(() => _concepts.Resolve("value"));
    }

    /// <summary>
    /// Emit a source-blind primary edge with 2 role-typed members.
    /// edge_hash = CompositionId([edge_type_hash, sourceParticipant, targetParticipant]).
    /// Returns the edge hash so the caller can attach metadata.
    /// </summary>
    public async ValueTask<AtomId> EmitEdgeAsync(
        AtomId            edgeTypeHash,
        AtomId            sourceParticipant,
        AtomId            targetParticipant,
        CancellationToken cancellationToken)
    {
        var edgeHash = _hashing.CompositionId(
            new List<AtomId> { edgeTypeHash, sourceParticipant, targetParticipant },
            new List<int>    { 1, 1, 1 });

        await _edges.EmitEdgeAsync(
            new EdgeRecord(edgeTypeHash, edgeHash),
            cancellationToken).ConfigureAwait(false);

        await _edges.EmitMemberAsync(
            new EdgeMemberRecord(edgeTypeHash, edgeHash, _sourceRole.Value, 0, sourceParticipant),
            cancellationToken).ConfigureAwait(false);

        await _edges.EmitMemberAsync(
            new EdgeMemberRecord(edgeTypeHash, edgeHash, _targetRole.Value, 0, targetParticipant),
            cancellationToken).ConfigureAwait(false);

        return edgeHash;
    }

    /// <summary>
    /// Emit a provenance attestation for the (edgeTypeHash, edgeHash) edge:
    ///   - Composition entity over <paramref name="provenanceCompositionAtoms"/>
    ///     (typically [modelEntity, attestorEntity, ingestionTimestampAtom])
    ///   - edge_provenance row linking the edge to the composition entity
    ///   - One from_X meta-edge per attestor: provenance_entity → fromKindAtom → attestorEntity
    /// Returns the provenance entity hash for downstream attachment if needed.
    /// </summary>
    public async ValueTask<AtomId> EmitProvenanceAsync(
        AtomId                                                              edgeTypeHash,
        AtomId                                                              edgeHash,
        IReadOnlyList<AtomId>                                               provenanceCompositionAtoms,
        IReadOnlyList<(AtomId AttestorEntity, AtomId FromKindAtom)>         attestors,
        CancellationToken                                                   cancellationToken)
    {
        var rleCounts = new int[provenanceCompositionAtoms.Count];
        for (var i = 0; i < rleCounts.Length; i++) { rleCounts[i] = 1; }

        var provenanceEntity = _hashing.CompositionId(provenanceCompositionAtoms, rleCounts);

        await _provenance.EmitEdgeProvenanceAsync(
            new EdgeProvenanceRecord(edgeTypeHash, edgeHash, provenanceEntity),
            cancellationToken).ConfigureAwait(false);

        foreach (var (attestorEntity, fromKindAtom) in attestors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EmitProvenanceMetaEdgeAsync(
                fromKindAtom, provenanceEntity, attestorEntity, cancellationToken).ConfigureAwait(false);
        }

        return provenanceEntity;
    }

    /// <summary>
    /// Emit a has_magnitude meta-edge from the carrier edge to the
    /// F2-decomposed magnitude entity (the recursive edge-as-entity pattern
    /// that mirrors POS/sense disambiguation on linguistic edges).
    /// </summary>
    public async ValueTask EmitHasMagnitudeAsync(
        AtomId            edgeBeingAnnotated,
        AtomId            magnitudeEntity,
        CancellationToken cancellationToken)
    {
        var metaType = _hasMagnitudeKind.Value;
        var metaEdgeHash = _hashing.CompositionId(
            new List<AtomId> { metaType, edgeBeingAnnotated, magnitudeEntity },
            new List<int>    { 1, 1, 1 });

        await _edges.EmitEdgeAsync(
            new EdgeRecord(metaType, metaEdgeHash),
            cancellationToken).ConfigureAwait(false);

        await _edges.EmitMemberAsync(
            new EdgeMemberRecord(metaType, metaEdgeHash, _carrierRole.Value, 0, edgeBeingAnnotated),
            cancellationToken).ConfigureAwait(false);

        await _edges.EmitMemberAsync(
            new EdgeMemberRecord(metaType, metaEdgeHash, _valueRole.Value, 0, magnitudeEntity),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EmitProvenanceMetaEdgeAsync(
        AtomId            metaEdgeTypeHash,
        AtomId            provenanceEntity,
        AtomId            attestorEntity,
        CancellationToken cancellationToken)
    {
        var metaEdgeHash = _hashing.CompositionId(
            new List<AtomId> { metaEdgeTypeHash, provenanceEntity, attestorEntity },
            new List<int>    { 1, 1, 1 });

        await _edges.EmitEdgeAsync(
            new EdgeRecord(metaEdgeTypeHash, metaEdgeHash),
            cancellationToken).ConfigureAwait(false);

        await _edges.EmitMemberAsync(
            new EdgeMemberRecord(metaEdgeTypeHash, metaEdgeHash, _sourceRole.Value, 0, provenanceEntity),
            cancellationToken).ConfigureAwait(false);

        await _edges.EmitMemberAsync(
            new EdgeMemberRecord(metaEdgeTypeHash, metaEdgeHash, _targetRole.Value, 0, attestorEntity),
            cancellationToken).ConfigureAwait(false);
    }
}
