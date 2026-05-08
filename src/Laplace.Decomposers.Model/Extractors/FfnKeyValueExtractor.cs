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
/// Second of #38's eleven per-tensor extractors — Feed-Forward Network
/// key-value framing per Geva 2021 ("Transformer Feed-Forward Layers Are
/// Key-Value Memories"). Each FFN neuron N is its own mechanistic substrate
/// entity (Llama_L3_N42 = composition over [mechanistic_neuron_role,
/// model_entity, F2(layer), F2(neuron)]); it holds its own POINT4D
/// operator shapes (W_up row and W_down column) in the model_weights_4d
/// physicality partition.
///
/// Two source-blind edge kinds:
///   - `ffn_key_activates`: pair = (input_pattern_entity, neuron_entity).
///     W_up rows describe which input activates which neuron; magnitude =
///     activation strength.
///   - `ffn_value_writes`: pair = (neuron_entity, output_feature_entity).
///     W_down columns describe what output distribution each neuron writes
///     when activated; magnitude = write strength.
///
/// Phase 4 / Track F5 / G5.
/// </summary>
public sealed class FfnKeyValueExtractor
{
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _concepts;
    private readonly IPhysicalityEmission   _physicality;
    private readonly IProvenance            _provenance;
    private readonly SourceBlindEdgeEmitter _edgeEmitter;

    private readonly Lazy<AtomId> _modelWeights4dPhysicalityType;

    private readonly Lazy<AtomId> _upKeyRole;
    private readonly Lazy<AtomId> _downValueRole;

    private readonly Lazy<AtomId> _ffnKeyActivatesKind;
    private readonly Lazy<AtomId> _ffnValueWritesKind;

    private readonly Lazy<AtomId> _fromModelKind;
    private readonly Lazy<AtomId> _fromNeuronKind;

    public FfnKeyValueExtractor(
        MechanisticHeadEntityResolver heads,
        IIdentityHashing              hashing,
        IConceptEntityResolver        concepts,
        IPhysicalityEmission          physicality,
        IEdgeEmission                 edges,
        IProvenance                   provenance)
    {
        _ = heads ?? throw new ArgumentNullException(nameof(heads));
        _hashing     = hashing     ?? throw new ArgumentNullException(nameof(hashing));
        _concepts    = concepts    ?? throw new ArgumentNullException(nameof(concepts));
        _physicality = physicality ?? throw new ArgumentNullException(nameof(physicality));
        _provenance  = provenance  ?? throw new ArgumentNullException(nameof(provenance));

        _edgeEmitter = new SourceBlindEdgeEmitter(hashing, concepts, edges, provenance);

        _modelWeights4dPhysicalityType = new Lazy<AtomId>(() => _concepts.Resolve("model_weights_4d"));

        _upKeyRole     = new Lazy<AtomId>(() => _concepts.Resolve("ffn_up_key"));
        _downValueRole = new Lazy<AtomId>(() => _concepts.Resolve("ffn_down_value"));

        _ffnKeyActivatesKind = new Lazy<AtomId>(() => _concepts.Resolve("ffn_key_activates"));
        _ffnValueWritesKind  = new Lazy<AtomId>(() => _concepts.Resolve("ffn_value_writes"));

        _fromModelKind  = new Lazy<AtomId>(() => _concepts.Resolve("from_model"));
        _fromNeuronKind = new Lazy<AtomId>(() => _concepts.Resolve("from_neuron"));
    }

    /// <summary>
    /// Project a per-neuron W_up row or W_down column to POINT4D and emit
    /// it as a PhysicalityRecord into the model_weights_4d partition.
    /// Entity = composition over (role, neuron_entity).
    /// </summary>
    public async ValueTask<AtomId> EmitNeuronOperatorShapeAsync(
        AtomId                 modelEntity,
        string                 modelSourceCanonicalName,
        FfnNeuronRoleKind      role,
        AtomId                 neuronEntity,
        ReadOnlyMemory<double> vector,
        CancellationToken      cancellationToken)
    {
        var roleAtom = ResolveRoleAtom(role);

        var operatorShapeEntity = _hashing.CompositionId(
            new List<AtomId> { roleAtom, neuronEntity },
            new List<int>    { 1, 1 });

        var point    = VectorToPoint4D.Project(vector.Span);
        var geometry = new[] { point };

        var sourceHash = await _provenance
            .ResolveSourceAsync(modelSourceCanonicalName, cancellationToken)
            .ConfigureAwait(false);

        await _physicality.EmitAsync(
            new PhysicalityRecord(
                PhysicalityTypeHash: _modelWeights4dPhysicalityType.Value,
                EntityHash:          operatorShapeEntity,
                SourceHash:          sourceHash,
                Geometry:            geometry),
            cancellationToken).ConfigureAwait(false);

        return operatorShapeEntity;
    }

    /// <summary>
    /// Source-blind `ffn_key_activates` edge for an observed
    /// (input_pattern → neuron) firing.
    /// </summary>
    public ValueTask<AtomId> EmitKeyActivatesEdgeAsync(
        AtomId            modelEntity,
        AtomId            neuronEntity,
        AtomId            ingestionTimestampAtom,
        AtomId            inputPatternEntity,
        AtomId            magnitudeEntity,
        CancellationToken cancellationToken)
        => EmitDiscreteEdgeAsync(
            _ffnKeyActivatesKind.Value,
            modelEntity, neuronEntity, ingestionTimestampAtom,
            sourceParticipant: inputPatternEntity,
            targetParticipant: neuronEntity,
            magnitudeEntity,
            cancellationToken);

    /// <summary>
    /// Source-blind `ffn_value_writes` edge for an observed
    /// (neuron → output_feature) write distribution.
    /// </summary>
    public ValueTask<AtomId> EmitValueWritesEdgeAsync(
        AtomId            modelEntity,
        AtomId            neuronEntity,
        AtomId            ingestionTimestampAtom,
        AtomId            outputFeatureEntity,
        AtomId            magnitudeEntity,
        CancellationToken cancellationToken)
        => EmitDiscreteEdgeAsync(
            _ffnValueWritesKind.Value,
            modelEntity, neuronEntity, ingestionTimestampAtom,
            sourceParticipant: neuronEntity,
            targetParticipant: outputFeatureEntity,
            magnitudeEntity,
            cancellationToken);

    private async ValueTask<AtomId> EmitDiscreteEdgeAsync(
        AtomId            edgeTypeHash,
        AtomId            modelEntity,
        AtomId            neuronEntity,
        AtomId            ingestionTimestampAtom,
        AtomId            sourceParticipant,
        AtomId            targetParticipant,
        AtomId            magnitudeEntity,
        CancellationToken cancellationToken)
    {
        var edgeHash = await _edgeEmitter.EmitEdgeAsync(
            edgeTypeHash, sourceParticipant, targetParticipant, cancellationToken).ConfigureAwait(false);

        await _edgeEmitter.EmitProvenanceAsync(
            edgeTypeHash, edgeHash,
            new[] { modelEntity, neuronEntity, ingestionTimestampAtom },
            new[] { (modelEntity, _fromModelKind.Value), (neuronEntity, _fromNeuronKind.Value) },
            cancellationToken).ConfigureAwait(false);

        await _edgeEmitter.EmitHasMagnitudeAsync(
            edgeHash, magnitudeEntity, cancellationToken).ConfigureAwait(false);

        return edgeHash;
    }

    private AtomId ResolveRoleAtom(FfnNeuronRoleKind role)
        => role switch
        {
            FfnNeuronRoleKind.UpKey     => _upKeyRole.Value,
            FfnNeuronRoleKind.DownValue => _downValueRole.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown FFN neuron role."),
        };
}
