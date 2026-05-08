namespace Laplace.Decomposers.Model.EdgeMetadata;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// Emits meta-edges that hang off a primary substrate edge — the recursive
/// entity-edge structure that lets a tensor-extracted edge carry its own
/// magnitude / Glicko-2 state / circuit membership / polysemy mode /
/// context-firing observations as first-class substrate citizens (same
/// pattern as POS/sense disambiguation on linguistic edges).
///
/// Per substrate invariant 4 + the edges-carry-metadata-graphs invariant:
/// every per-tensor extracted edge is an entity, and edges can have their
/// own outgoing edges. EdgeMetadataGraphEmitter is the canonical emission
/// path for those meta-edges so per-tensor extractors don't repeat the
/// composition logic.
///
/// Meta-edge identity is source-blind (kind only): has_magnitude /
/// has_glicko2_state / participates_in_circuit / polysemy_mode meta-edges
/// share a single edge_type per kind across all attestors. Multi-source
/// attestation accumulates as separate provenance attestations on the
/// same shared meta-edge (per the source-blind-edges invariant).
///
/// Phase 4 / Track F5 / supports #38 per-tensor extractors.
/// </summary>
public sealed class EdgeMetadataGraphEmitter
{
    private readonly IIdentityHashing       _hashing;
    private readonly IConceptEntityResolver _concepts;
    private readonly IEdgeEmission          _edges;

    private readonly Lazy<AtomId> _hasMagnitudeType;
    private readonly Lazy<AtomId> _hasGlicko2StateType;
    private readonly Lazy<AtomId> _participatesInCircuitType;
    private readonly Lazy<AtomId> _polysemyModeType;
    private readonly Lazy<AtomId> _firesInContextType;

    private readonly Lazy<AtomId> _carrierRole;
    private readonly Lazy<AtomId> _valueRole;

    public EdgeMetadataGraphEmitter(
        IIdentityHashing       hashing,
        IConceptEntityResolver concepts,
        IEdgeEmission          edges)
    {
        _hashing  = hashing  ?? throw new ArgumentNullException(nameof(hashing));
        _concepts = concepts ?? throw new ArgumentNullException(nameof(concepts));
        _edges    = edges    ?? throw new ArgumentNullException(nameof(edges));

        _hasMagnitudeType          = new Lazy<AtomId>(() => _concepts.Resolve("has_magnitude"));
        _hasGlicko2StateType       = new Lazy<AtomId>(() => _concepts.Resolve("has_glicko2_state"));
        _participatesInCircuitType = new Lazy<AtomId>(() => _concepts.Resolve("participates_in_circuit"));
        _polysemyModeType          = new Lazy<AtomId>(() => _concepts.Resolve("polysemy_mode"));
        _firesInContextType        = new Lazy<AtomId>(() => _concepts.Resolve("fires_in_context"));

        _carrierRole = new Lazy<AtomId>(() => _concepts.Resolve("carrier"));
        _valueRole   = new Lazy<AtomId>(() => _concepts.Resolve("value"));
    }

    /// <summary>
    /// Emit `(edgeBeingAnnotated) → has_magnitude → numberEntity`. The
    /// number entity is an F2 INumberDecomposition result (digit-codepoint
    /// LINESTRING + RLE). Returns the meta-edge's content-addressed hash.
    /// </summary>
    public ValueTask<AtomId> EmitHasMagnitudeAsync(
        AtomId            edgeBeingAnnotated,
        AtomId            numberEntity,
        CancellationToken cancellationToken)
        => EmitMetaEdgeAsync(_hasMagnitudeType.Value, edgeBeingAnnotated, numberEntity, cancellationToken);

    /// <summary>
    /// Emit `(edgeBeingAnnotated) → has_glicko2_state → ratingComposition`. The
    /// rating composition entity is a tier-1 composition over (mu, phi, sigma,
    /// games) F2 number entities, so substrate queries can intersect Glicko-2
    /// state geometrically.
    /// </summary>
    public ValueTask<AtomId> EmitHasGlicko2StateAsync(
        AtomId            edgeBeingAnnotated,
        AtomId            ratingComposition,
        CancellationToken cancellationToken)
        => EmitMetaEdgeAsync(_hasGlicko2StateType.Value, edgeBeingAnnotated, ratingComposition, cancellationToken);

    /// <summary>
    /// Emit `(edgeBeingAnnotated) → participates_in_circuit → circuitEntity`.
    /// Circuit entities are compositions over sibling edges identified by
    /// mech-interp literature (induction heads, name-mover heads, factual-
    /// recall circuits per Geva 2021 / Olah / Anthropic interpretability).
    /// </summary>
    public ValueTask<AtomId> EmitParticipatesInCircuitAsync(
        AtomId            edgeBeingAnnotated,
        AtomId            circuitEntity,
        CancellationToken cancellationToken)
        => EmitMetaEdgeAsync(_participatesInCircuitType.Value, edgeBeingAnnotated, circuitEntity, cancellationToken);

    /// <summary>
    /// Emit `(edgeBeingAnnotated) → polysemy_mode → senseEntity`. Lets the
    /// substrate disambiguate the edge's interpretation per sense (the
    /// attention edge `cat → mammal` resolves as biological-classification
    /// vs metaphorical-stretch via different mode entities each with their
    /// own conditional Glicko-2 ratings).
    /// </summary>
    public ValueTask<AtomId> EmitPolysemyModeAsync(
        AtomId            edgeBeingAnnotated,
        AtomId            senseEntity,
        CancellationToken cancellationToken)
        => EmitMetaEdgeAsync(_polysemyModeType.Value, edgeBeingAnnotated, senseEntity, cancellationToken);

    /// <summary>
    /// Emit `(edgeBeingAnnotated) → fires_in_context → contextOrTrajectoryEntity`.
    /// Populated by substrate forward-pass observations (Phase 5) — at extraction
    /// time this is unused, at query time it accumulates as trajectories traverse
    /// the edge.
    /// </summary>
    public ValueTask<AtomId> EmitFiresInContextAsync(
        AtomId            edgeBeingAnnotated,
        AtomId            contextOrTrajectoryEntity,
        CancellationToken cancellationToken)
        => EmitMetaEdgeAsync(_firesInContextType.Value, edgeBeingAnnotated, contextOrTrajectoryEntity, cancellationToken);

    private async ValueTask<AtomId> EmitMetaEdgeAsync(
        AtomId            metaEdgeTypeHash,
        AtomId            carrierEdgeHash,
        AtomId            valueEntityHash,
        CancellationToken cancellationToken)
    {
        var roleOrdered = new (AtomId Role, int RolePosition, AtomId Participant)[]
        {
            (_carrierRole.Value, 0, carrierEdgeHash),
            (_valueRole.Value,   0, valueEntityHash),
        };
        var metaEdgeHash = _hashing.EdgeId(metaEdgeTypeHash, roleOrdered);

        await _edges.EmitEdgeAsync(
            new EdgeRecord(metaEdgeTypeHash, metaEdgeHash),
            cancellationToken).ConfigureAwait(false);

        await _edges.EmitMemberAsync(
            new EdgeMemberRecord(metaEdgeTypeHash, metaEdgeHash, _carrierRole.Value, 0, carrierEdgeHash),
            cancellationToken).ConfigureAwait(false);

        await _edges.EmitMemberAsync(
            new EdgeMemberRecord(metaEdgeTypeHash, metaEdgeHash, _valueRole.Value, 0, valueEntityHash),
            cancellationToken).ConfigureAwait(false);

        return metaEdgeHash;
    }
}
