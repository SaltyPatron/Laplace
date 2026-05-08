namespace Laplace.Decomposers.Model.Extractors;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Model.EdgeMetadata;
using Laplace.Decomposers.Model.Mechanistic;
using Laplace.Decomposers.Model.OperatorShapes;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// First of #38's eleven per-tensor extractors. Two responsibilities, both
/// keyed on mechanistic head entities (Llama_L3H7-style) so substrate
/// growth stays sublinear with model count and the discrete attention
/// edges remain source-blind:
///
///   1. <see cref="EmitOperatorShapeAsync"/> — projects W_Q / W_K / W_V /
///      W_O matrices to LINESTRING4D and emits one PhysicalityRecord per
///      (matrix_kind, head_entity) into the model_weights_4d physicality
///      partition. Enables ST_FrechetDistance4D-driven cross-model circuit
///      discovery, surgical head replacement, model archaeology, and the
///      geometric matmul replacement at re-export time.
///
///   2. <see cref="EmitDiscreteEdgeAsync"/> — given a (queryTokenEntity,
///      keyTokenEntity, magnitudeEntity) attestation observed during F5
///      ingestion activation observation, emit the source-BLIND `attends`
///      edge with kind composition over [attends_kind] only (NEVER source-
///      tagged with model/layer/head); a provenance attestation entity
///      composing [modelEntity, headEntity, ingestionTimestampAtom] with
///      from_model + from_head meta-edges; and the has_magnitude meta-edge.
///
///      The same (queryTokenEntity, keyTokenEntity) pair attested by N
///      different (model, layer, head) triples produces ONE edge with N
///      provenance attestations and cumulative Glicko-2 — substrate is
///      sublinear in model count, consensus emerges directly on the
///      shared edge's rating.
///
/// Phase 4 / Track F5 / G5. Used by EncoderOnly / DecoderOnly /
/// EncoderDecoder / VisionEncoder / Multimodal architecture-family
/// decomposers (#37).
/// </summary>
public sealed class AttentionEdgeExtractor
{
    private readonly MatrixToLineString4D    _matrixProjection;
    private readonly IIdentityHashing        _hashing;
    private readonly IConceptEntityResolver  _concepts;
    private readonly IPhysicalityEmission    _physicality;
    private readonly IProvenance             _provenance;
    private readonly SourceBlindEdgeEmitter  _edgeEmitter;

    private readonly Lazy<AtomId> _modelWeights4dPhysicalityType;

    private readonly Lazy<AtomId> _wqRole;
    private readonly Lazy<AtomId> _wkRole;
    private readonly Lazy<AtomId> _wvRole;
    private readonly Lazy<AtomId> _woRole;

    private readonly Lazy<AtomId> _attendsKind;

    private readonly Lazy<AtomId> _fromModelKind;
    private readonly Lazy<AtomId> _fromHeadKind;

    public AttentionEdgeExtractor(
        MechanisticHeadEntityResolver heads,
        MatrixToLineString4D          matrixProjection,
        IIdentityHashing              hashing,
        IConceptEntityResolver        concepts,
        IPhysicalityEmission          physicality,
        IEdgeEmission                 edges,
        IProvenance                   provenance)
    {
        _ = heads ?? throw new ArgumentNullException(nameof(heads));
        _matrixProjection = matrixProjection ?? throw new ArgumentNullException(nameof(matrixProjection));
        _hashing          = hashing          ?? throw new ArgumentNullException(nameof(hashing));
        _concepts         = concepts         ?? throw new ArgumentNullException(nameof(concepts));
        _physicality      = physicality      ?? throw new ArgumentNullException(nameof(physicality));
        _provenance       = provenance       ?? throw new ArgumentNullException(nameof(provenance));

        _edgeEmitter = new SourceBlindEdgeEmitter(hashing, concepts, edges, provenance);

        _modelWeights4dPhysicalityType = new Lazy<AtomId>(() => _concepts.Resolve("model_weights_4d"));

        _wqRole = new Lazy<AtomId>(() => _concepts.Resolve("attention_wq"));
        _wkRole = new Lazy<AtomId>(() => _concepts.Resolve("attention_wk"));
        _wvRole = new Lazy<AtomId>(() => _concepts.Resolve("attention_wv"));
        _woRole = new Lazy<AtomId>(() => _concepts.Resolve("attention_wo"));

        _attendsKind = new Lazy<AtomId>(() => _concepts.Resolve("attends"));

        _fromModelKind = new Lazy<AtomId>(() => _concepts.Resolve("from_model"));
        _fromHeadKind  = new Lazy<AtomId>(() => _concepts.Resolve("from_head"));
    }

    /// <summary>
    /// Project a per-head attention matrix slice (W_Q / W_K / W_V / W_O for
    /// a specific (layer L, head H)) to a LINESTRING4D and emit it into the
    /// model_weights_4d partition. Entity = composition over (matrix_role,
    /// head_entity), so the same head's W_Q across two ingestions of the
    /// same model dedupes automatically.
    /// </summary>
    public async ValueTask<AtomId> EmitOperatorShapeAsync(
        AtomId                 modelEntity,
        string                 modelSourceCanonicalName,
        AttentionMatrixKind    matrixKind,
        AtomId                 headEntity,
        ReadOnlyMemory<double> matrix,
        int                    rowCount,
        int                    columnCount,
        CancellationToken      cancellationToken)
    {
        var matrixRoleAtom = ResolveMatrixRoleAtom(matrixKind);

        var operatorShapeEntity = _hashing.CompositionId(
            new List<AtomId> { matrixRoleAtom, headEntity },
            new List<int>    { 1, 1 });

        var lineString = _matrixProjection.Project(matrix, rowCount, columnCount);

        var sourceHash = await _provenance
            .ResolveSourceAsync(modelSourceCanonicalName, cancellationToken)
            .ConfigureAwait(false);

        await _physicality.EmitAsync(
            new PhysicalityRecord(
                PhysicalityTypeHash: _modelWeights4dPhysicalityType.Value,
                EntityHash:          operatorShapeEntity,
                SourceHash:          sourceHash,
                Geometry:            lineString),
            cancellationToken).ConfigureAwait(false);

        return operatorShapeEntity;
    }

    /// <summary>
    /// Emit the source-blind `attends` edge for an observed attention
    /// firing — (queryTokenEntity, keyTokenEntity, magnitudeEntity) —
    /// together with the cumulative-attestation provenance entity
    /// (composition over modelEntity + headEntity + ingestionTimestampAtom)
    /// plus its from_model and from_head meta-edges, and the has_magnitude
    /// meta-edge from the edge to the F2-decomposed weight magnitude
    /// entity. Glicko-2 update on the shared edge is the caller's
    /// responsibility (driven by ISignificance from the ingestion driver).
    /// </summary>
    public async ValueTask<AtomId> EmitDiscreteEdgeAsync(
        AtomId            modelEntity,
        AtomId            headEntity,
        AtomId            ingestionTimestampAtom,
        AtomId            queryTokenEntity,
        AtomId            keyTokenEntity,
        AtomId            weightMagnitudeEntity,
        CancellationToken cancellationToken)
    {
        var attendsType = _attendsKind.Value;

        var edgeHash = await _edgeEmitter.EmitEdgeAsync(
            attendsType, queryTokenEntity, keyTokenEntity, cancellationToken).ConfigureAwait(false);

        await _edgeEmitter.EmitProvenanceAsync(
            attendsType, edgeHash,
            new[] { modelEntity, headEntity, ingestionTimestampAtom },
            new[] { (modelEntity, _fromModelKind.Value), (headEntity, _fromHeadKind.Value) },
            cancellationToken).ConfigureAwait(false);

        await _edgeEmitter.EmitHasMagnitudeAsync(
            edgeHash, weightMagnitudeEntity, cancellationToken).ConfigureAwait(false);

        return edgeHash;
    }

    private AtomId ResolveMatrixRoleAtom(AttentionMatrixKind matrixKind)
        => matrixKind switch
        {
            AttentionMatrixKind.Query  => _wqRole.Value,
            AttentionMatrixKind.Key    => _wkRole.Value,
            AttentionMatrixKind.Value  => _wvRole.Value,
            AttentionMatrixKind.Output => _woRole.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(matrixKind), matrixKind, "Unknown attention matrix kind.")
        };
}
