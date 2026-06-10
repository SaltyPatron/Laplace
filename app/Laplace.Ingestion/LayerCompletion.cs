using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

public static class LayerCompletion
{
    public static Hash128 RelationTypeId(int layerOrder) =>
        Hash128.OfCanonical($"substrate/type/HasLayerCompleted/{layerOrder}/v1");

    public static SubstrateChange BuildMarker(IDecomposer decomposer)
    {
        var typeId = RelationTypeId(decomposer.LayerOrder);
        return new SubstrateChangeBuilder(
                decomposer.SourceId, $"layer-complete/{decomposer.LayerOrder}", null,
                entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 1)
            .AddEntity(typeId, EntityTier.Vocabulary, BootstrapIntentBuilder.RelationTypeMetaTypeId, decomposer.SourceId)
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
