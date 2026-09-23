using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

public static class LayerCompletion
{
    /// <summary>
    /// Governance envelope for ingest-layer marker relation ids. These markers are operational
    /// metadata, never rated testimony, so ConsensusAccumulatingWriter precomputes every id in
    /// this range and excludes it from folding. The old ceiling of 8 predated the chess lanes
    /// (20-24): their HasLayerCompleted markers therefore leaked into consensus even though the
    /// comments and reader contract said they could not. Keep a deliberately bounded byte-wide
    /// namespace and fail closed if a future lane attempts to escape it.
    /// </summary>
    public const int MaxMarkedLayer = byte.MaxValue;

    public static Hash128 RelationTypeId(int layerOrder)
    {
        if ((uint)layerOrder > MaxMarkedLayer)
            throw new ArgumentOutOfRangeException(
                nameof(layerOrder), layerOrder,
                $"ingest layer must be in 0..{MaxMarkedLayer} so completion markers remain excluded from consensus");
        return Hash128.OfCanonical($"substrate/type/HasLayerCompleted/{layerOrder}/v1");
    }

    /// <summary>
    /// Per-file completion marker (Pillar 0): subject/object/source are all the
    /// file-entity's content-DAG root, so re-ingesting the same file collides on the
    /// marker's attestation identity and <c>HasSourceCompletedAsync(root, layer)</c>
    /// answers "has this exact file finished?" before any compose work. Emitted into
    /// the same change as the file's content, so marker-present implies
    /// content-committed.
    /// </summary>
    public static void EmitFileMarker(SubstrateChangeBuilder builder, Hash128 fileRoot, int layerOrder)
    {
        var typeId = RelationTypeId(layerOrder);
        PlaceMarker(builder, typeId, fileRoot, layerOrder);
        builder.AddAttestation(NativeAttestation.CategoricalResolved(
            fileRoot, typeId, fileRoot, fileRoot, contextId: null,
            RelationTypeRank.Mandate * SourceTrust.SubstrateMandate));
    }

    /// <summary>Vendor-owned per-file marker. File identity remains the provenance
    /// source; context identifies the specific decomposer implementation that completed
    /// it, so another vendor consuming the same bytes cannot inherit its checkpoint.</summary>
    public static void EmitFileMarker(
        SubstrateChangeBuilder builder, Hash128 fileRoot, Hash128 decomposerSourceId,
        int layerOrder)
    {
        var typeId = RelationTypeId(layerOrder);
        PlaceMarker(builder, typeId, fileRoot, layerOrder);
        builder.AddAttestation(NativeAttestation.CategoricalResolved(
            fileRoot, typeId, fileRoot, fileRoot, contextId: decomposerSourceId,
            RelationTypeRank.Mandate * SourceTrust.SubstrateMandate));
    }

    public static SubstrateChange BuildMarker(IDecomposer decomposer)
    {
        var typeId = RelationTypeId(decomposer.LayerOrder);
        var builder = new SubstrateChangeBuilder(
                decomposer.SourceId, $"layer-complete/{decomposer.LayerOrder}", null,
                entityCapacity: 8, physicalityCapacity: 4, attestationCapacity: 1)
            .DeclareSourcePrior(SourceTrust.SubstrateMandate);
        PlaceMarker(builder, typeId, decomposer.SourceId, decomposer.LayerOrder);
        return builder
            .AddAttestation(NativeAttestation.CategoricalResolved(
                decomposer.SourceId,
                typeId,
                decomposer.SourceId,
                decomposer.SourceId,
                contextId: null,
                RelationTypeRank.Mandate * SourceTrust.SubstrateMandate))
            .Build();
    }

    // The marker id is a governed operator key. Closure counts that entity row,
    // so the key is realized through the same named-identity projection as every
    // other governed id. The attestation stays operational and is not testimony.
    // The realized name's physicalities are observed by the marker's owner. An owner
    // that already declared its prior in this unit keeps it; otherwise the marker
    // declares the mandate prior every physicality observation requires.
    private static void PlaceMarker(
        SubstrateChangeBuilder builder, Hash128 typeId, Hash128 observedBy, int layerOrder)
    {
        if (!builder.HasSourcePrior(observedBy))
            builder.DeclareSourcePrior(observedBy, SourceTrust.SubstrateMandate);
        CanonicalNamedIdentity.Declare(
            builder,
            typeId,
            EntityTier.Word,
            BootstrapIntentBuilder.RelationTypeMetaTypeId,
            $"substrate/type/HasLayerCompleted/{layerOrder}/v1",
            observedBy);
    }
}
