namespace Laplace.Decomposers.Model.Mechanistic;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// Content-addresses model-specific mechanistic head / neuron / expert entities.
/// Llama_L3H7_hash = CompositionId([mechanistic_head_role, llama_model_entity, number_3, number_7]);
/// the entity is a first-class substrate citizen that serves as the from_head provenance
/// source on every edge it attests during F5 ingestion, and holds its W_Q / W_K / W_V / W_O
/// LINESTRING4D operator shapes in the model_weights_4d physicality partition. Cross-model
/// circuit discovery operates on these head-entity LINESTRINGs, not on the source-blind
/// semantic edges they attest (substrate growth stays sublinear with model count).
///
/// Number entities for layer / head / neuron / expert indexes flow through F1 TextDecomposer
/// so they coincide with F2's canonical number-entity hashes — the layer-3 entity inside a
/// head's composition IS THE SAME ENTITY as the substrate-wide "number 3" used elsewhere
/// (substrate invariant 1: same content, same hash).
///
/// Discriminator role atoms (mechanistic_head / mechanistic_neuron / mechanistic_expert)
/// prevent collisions when (layer, index) tuples coincide across head and neuron domains
/// within the same model.
///
/// Pure-identity service: returns AtomId only. Emission (entity record, physicality record
/// with centroid, LINESTRING4D operator shapes) is the per-tensor extractor's responsibility.
///
/// Phase 4 / Track F5 / supports #38 per-tensor extractors.
/// </summary>
public sealed class MechanisticHeadEntityResolver
{
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _concepts;
    private readonly TextDecomposer         _text;

    private readonly Lazy<AtomId> _headRole;
    private readonly Lazy<AtomId> _neuronRole;
    private readonly Lazy<AtomId> _expertRole;

    private readonly ConcurrentDictionary<int, AtomId> _intCache = new();

    public MechanisticHeadEntityResolver(
        IIdentityHashing       hashing,
        IConceptEntityResolver concepts,
        TextDecomposer         text)
    {
        _hashing  = hashing  ?? throw new ArgumentNullException(nameof(hashing));
        _concepts = concepts ?? throw new ArgumentNullException(nameof(concepts));
        _text     = text     ?? throw new ArgumentNullException(nameof(text));

        _headRole   = new Lazy<AtomId>(() => _concepts.Resolve("mechanistic_head"));
        _neuronRole = new Lazy<AtomId>(() => _concepts.Resolve("mechanistic_neuron"));
        _expertRole = new Lazy<AtomId>(() => _concepts.Resolve("mechanistic_expert"));
    }

    /// <summary>Mechanistic attention head (Llama_L3H7-style) entity hash.</summary>
    public async Task<AtomId> ResolveHeadAsync(
        AtomId            modelEntity,
        int               layerIndex,
        int               headIndex,
        CancellationToken cancellationToken)
    {
        var layerAtom = await IntegerAtomAsync(layerIndex, cancellationToken).ConfigureAwait(false);
        var headAtom  = await IntegerAtomAsync(headIndex,  cancellationToken).ConfigureAwait(false);
        return _hashing.CompositionId(
            new List<AtomId> { _headRole.Value, modelEntity, layerAtom, headAtom },
            new List<int>    { 1, 1, 1, 1 });
    }

    /// <summary>Mechanistic FFN neuron (Geva 2021 key-value framing) entity hash.</summary>
    public async Task<AtomId> ResolveNeuronAsync(
        AtomId            modelEntity,
        int               layerIndex,
        int               neuronIndex,
        CancellationToken cancellationToken)
    {
        var layerAtom  = await IntegerAtomAsync(layerIndex,  cancellationToken).ConfigureAwait(false);
        var neuronAtom = await IntegerAtomAsync(neuronIndex, cancellationToken).ConfigureAwait(false);
        return _hashing.CompositionId(
            new List<AtomId> { _neuronRole.Value, modelEntity, layerAtom, neuronAtom },
            new List<int>    { 1, 1, 1, 1 });
    }

    /// <summary>Mechanistic MoE expert entity hash (no layer index — experts are per-model).</summary>
    public async Task<AtomId> ResolveExpertAsync(
        AtomId            modelEntity,
        int               expertIndex,
        CancellationToken cancellationToken)
    {
        var expertAtom = await IntegerAtomAsync(expertIndex, cancellationToken).ConfigureAwait(false);
        return _hashing.CompositionId(
            new List<AtomId> { _expertRole.Value, modelEntity, expertAtom },
            new List<int>    { 1, 1, 1 });
    }

    /// <summary>
    /// Canonical substrate number-entity hash for an integer, via F1 TextDecomposer
    /// (digit-codepoint LINESTRING + RLE). Cached in-process; same int always
    /// produces the same hash AND that hash equals what F2 produces for the same
    /// number anywhere else in the substrate.
    /// </summary>
    public async Task<AtomId> IntegerAtomAsync(int n, CancellationToken cancellationToken)
    {
        if (_intCache.TryGetValue(n, out var cached))
        {
            return cached;
        }
        var atom = await _text.DecomposeAsync(
            n.ToString(CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        _intCache[n] = atom;
        return atom;
    }
}
