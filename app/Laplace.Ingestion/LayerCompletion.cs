using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// Records ADR 0037 layer completion via a <c>HasLayerCompleted/{n}</c>
/// attestation so <see cref="NpgsqlSubstrateReader.HasSourceEverCompletedAsync"/>
/// can gate later layers.
/// </summary>
internal static class LayerCompletion
{
    public static Hash128 KindId(int layerOrder) =>
        Hash128.OfCanonical($"substrate/kind/HasLayerCompleted/{layerOrder}/v1");

    public static SubstrateChange BuildMarker(IDecomposer decomposer)
    {
        // kind_id FK: every attestation kind must exist as an entity row first
        // (same rule as BootstrapIntentBuilder.AddKind / 10_bootstrap.sql.in).
        var kindId = KindId(decomposer.LayerOrder);
        return new SubstrateChangeBuilder(
                decomposer.SourceId, $"layer-complete/{decomposer.LayerOrder}", null,
                entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 1)
            .AddEntity(kindId, tier: 0, BootstrapIntentBuilder.KindMetaTypeId, decomposer.SourceId)
            .AddAttestation(AttestationFactory.Create(
                decomposer.SourceId,
                kindId,
                decomposer.SourceId,
                decomposer.SourceId,
                contextId: null,
                KindValueTier.T1,
                TrustClass.SubstrateMandateTier1))
            .Build();
    }
}
