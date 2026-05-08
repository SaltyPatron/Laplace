namespace Laplace.Decomposers.Model.Extractors;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Model.EdgeMetadata;
using Laplace.Decomposers.Model.OperatorShapes;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// Third of #38's eleven per-tensor extractors — the language modeling head
/// final projection. Maps the model's final-layer residual stream to vocab
/// token logits via W_lm_head : [hidden_dim, vocab_size].
///
/// One source-blind edge kind:
///   - `lm_predicts`: pair = (residual_pattern_entity, vocab_token_entity).
///
/// W_lm_head emits as a single LINESTRING4D operator shape into the
/// model_weights_4d partition, keyed by (lm_head_role, modelEntity).
/// Unlike Attention/FFN, no per-head/per-neuron mechanistic substrate
/// entity — the LM head is one wholesale operator per model. In many
/// architectures (BERT/Llama with `tie_word_embeddings: True`) this matrix
/// is the transpose of the embedding matrix; substrate dedup via content
/// addressing handles that automatically when the projection produces
/// identical LINESTRING geometry.
///
/// Phase 4 / Track F5 / G5.
/// </summary>
public sealed class LmHeadExtractor
{
    private readonly MatrixToLineString4D   _matrixProjection;
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _concepts;
    private readonly IPhysicalityEmission   _physicality;
    private readonly IProvenance            _provenance;
    private readonly SourceBlindEdgeEmitter _edgeEmitter;

    private readonly Lazy<AtomId> _modelWeights4dPhysicalityType;
    private readonly Lazy<AtomId> _lmHeadRole;
    private readonly Lazy<AtomId> _lmPredictsKind;
    private readonly Lazy<AtomId> _fromModelKind;

    public LmHeadExtractor(
        MatrixToLineString4D   matrixProjection,
        IIdentityHashing       hashing,
        IConceptEntityResolver concepts,
        IPhysicalityEmission   physicality,
        IEdgeEmission          edges,
        IProvenance            provenance)
    {
        _matrixProjection = matrixProjection ?? throw new ArgumentNullException(nameof(matrixProjection));
        _hashing          = hashing          ?? throw new ArgumentNullException(nameof(hashing));
        _concepts         = concepts         ?? throw new ArgumentNullException(nameof(concepts));
        _physicality      = physicality      ?? throw new ArgumentNullException(nameof(physicality));
        _provenance       = provenance       ?? throw new ArgumentNullException(nameof(provenance));

        _edgeEmitter = new SourceBlindEdgeEmitter(hashing, concepts, edges, provenance);

        _modelWeights4dPhysicalityType = new Lazy<AtomId>(() => _concepts.Resolve("model_weights_4d"));
        _lmHeadRole                    = new Lazy<AtomId>(() => _concepts.Resolve("lm_head"));
        _lmPredictsKind                = new Lazy<AtomId>(() => _concepts.Resolve("lm_predicts"));
        _fromModelKind                 = new Lazy<AtomId>(() => _concepts.Resolve("from_model"));
    }

    /// <summary>
    /// Project the W_lm_head matrix to a LINESTRING4D and emit it as a
    /// PhysicalityRecord into the model_weights_4d partition. Entity =
    /// composition over (lm_head_role, modelEntity).
    /// </summary>
    public async ValueTask<AtomId> EmitOperatorShapeAsync(
        AtomId                 modelEntity,
        string                 modelSourceCanonicalName,
        ReadOnlyMemory<double> matrix,
        int                    rowCount,
        int                    columnCount,
        CancellationToken      cancellationToken)
    {
        var operatorShapeEntity = _hashing.CompositionId(
            new List<AtomId> { _lmHeadRole.Value, modelEntity },
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
    /// Source-blind `lm_predicts` edge for an observed
    /// (residual_pattern → vocab_token) prediction. Provenance entity =
    /// composition over (modelEntity, ingestionTimestampAtom) — no
    /// mechanistic head/neuron attestor for LM head.
    /// </summary>
    public async ValueTask<AtomId> EmitDiscreteEdgeAsync(
        AtomId            modelEntity,
        AtomId            ingestionTimestampAtom,
        AtomId            residualPatternEntity,
        AtomId            vocabTokenEntity,
        AtomId            magnitudeEntity,
        CancellationToken cancellationToken)
    {
        var edgeTypeHash = _lmPredictsKind.Value;

        var edgeHash = await _edgeEmitter.EmitEdgeAsync(
            edgeTypeHash, residualPatternEntity, vocabTokenEntity, cancellationToken).ConfigureAwait(false);

        await _edgeEmitter.EmitProvenanceAsync(
            edgeTypeHash, edgeHash,
            new[] { modelEntity, ingestionTimestampAtom },
            new[] { (modelEntity, _fromModelKind.Value) },
            cancellationToken).ConfigureAwait(false);

        await _edgeEmitter.EmitHasMagnitudeAsync(
            edgeHash, magnitudeEntity, cancellationToken).ConfigureAwait(false);

        return edgeHash;
    }
}
