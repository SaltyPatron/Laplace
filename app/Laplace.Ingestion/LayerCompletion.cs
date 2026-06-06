using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion;

/// <summary>
/// Records layer completion via a <c>HasLayerCompleted/{n}</c>
/// attestation so <see cref="NpgsqlSubstrateReader.HasSourceEverCompletedAsync"/>
/// can gate later layers. Written ONLY on a clean run (zero failures), so it is
/// also the COMPLETION marker re-ingest guards key on: marker present = source
/// fully ingested (re-run refused — double-count); marker absent with partial
/// evidence = a killed run, and a re-run is lawful idempotent CONTINUATION
/// (dedup skims what landed, novel work proceeds, the period fold covers all).
/// Public so the CLI guard uses THIS kind id — never a re-derived string.
/// </summary>
public static class LayerCompletion
{
    public static Hash128 KindId(int layerOrder) =>
        Hash128.OfCanonical($"substrate/kind/HasLayerCompleted/{layerOrder}/v1");

    public static SubstrateChange BuildMarker(IDecomposer decomposer)
    {
        // type_id FK: every attestation kind must exist as an entity row first
        // (same rule as BootstrapIntentBuilder.AddTypeId / 10_bootstrap.sql.in).
        var typeId = KindId(decomposer.LayerOrder);
        return new SubstrateChangeBuilder(
                decomposer.SourceId, $"layer-complete/{decomposer.LayerOrder}", null,
                entityCapacity: 1, physicalityCapacity: 0, attestationCapacity: 1)
            .AddEntity(typeId, (byte)MetaTier.RelationType, BootstrapIntentBuilder.RelationTypeMetaTypeId, decomposer.SourceId)
            .AddAttestation(AttestationFactory.Create(
                decomposer.SourceId,
                typeId,
                decomposer.SourceId,
                decomposer.SourceId,
                contextId: null,
                RelationTypeRank.Mandate,
                SourceTrust.SubstrateMandate))
            .Build();
    }
}
