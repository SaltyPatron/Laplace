using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

public static class LayerCompletion
{
    /// <summary>Layers the marker relation is ever minted for; bounds the
    /// consensus-fold exclusion set (markers are ops metadata, never testimony).</summary>
    public const int MaxMarkedLayer = 8;

    public static Hash128 RelationTypeId(int layerOrder) =>
        Hash128.OfCanonical($"substrate/type/HasLayerCompleted/{layerOrder}/v1");

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
        builder
            .AddEntity(typeId, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, fileRoot)
            .AddAttestation(NativeAttestation.CategoricalResolved(
                fileRoot, typeId, fileRoot, fileRoot, contextId: null,
                RelationTypeRank.Mandate * SourceTrust.SubstrateMandate));
    }

    public static SubstrateChange BuildMarker(IDecomposer decomposer)
    {
        var typeId = RelationTypeId(decomposer.LayerOrder);
        return new SubstrateChangeBuilder(
                decomposer.SourceId, $"layer-complete/{decomposer.LayerOrder}", null,
                entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 1)
            .AddEntity(typeId, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, decomposer.SourceId)
            .AddAttestation(NativeAttestation.CategoricalResolved(
                decomposer.SourceId,
                typeId,
                decomposer.SourceId,
                decomposer.SourceId,
                contextId: null,
                RelationTypeRank.Mandate * SourceTrust.SubstrateMandate))
            .Build();
    }
}
