using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

// A Response is the substrate's OWN generated output, deposited as a content-addressed Document
// entity — symmetric to UserPromptContent. The generation walk builds a new GEOMETRYZM linestring
// (a new trajectory through the glome); depositing it makes generation reproducible (BLAKE3
// content address), citable (constituents trace to source trajectories), and self-extending (the
// output re-enters as corpus). It enters at LOW trust (SourceTrust.Response) so the system's own
// generations cannot pollute the high-trust ingested corpus — the trust hierarchy gates influence
// until a response is rated/validated, which also prevents self-ingestion model collapse.
public static class ResponseContent
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/Response/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/ResponseContent/v1");

    public static double WitnessWeight =>
        RelationTypeRank.Associative * SourceTrust.Response;

    public static SubstrateChange BuildBootstrapChange()
    {
        var b = new SubstrateChangeBuilder(Source, "bootstrap/Response", parentIntentId: null);
        b.AddEntity(Source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId, Source);
        b.AddEntity(TextEntityBuilder.GraphemeTypeId, EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        b.AddEntity(TextEntityBuilder.WordTypeId,     EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        b.AddEntity(TextEntityBuilder.SentenceTypeId, EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        b.AddEntity(TextEntityBuilder.DocumentTypeId, EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        return b.Build();
    }

    // Deposit a generated response. parentIntentId links provenance to the originating UserPrompt
    // (the prompt's deposited root), so prompt→response is auditable in the intent graph.
    public static bool TryBuildWitnessChange(
        byte[] utf8,
        string intentLabel,
        Hash128? parentIntentId,
        out SubstrateChange change,
        out Hash128 rootId)
    {
        if (!TextEntityBuilder.TryBuildContentWitness(utf8, Source, WitnessWeight,
                out var entities, out var physicalities, out var attestations, out rootId, out _))
        {
            change = default!;
            rootId = Hash128.Zero;
            return false;
        }

        var b = new SubstrateChangeBuilder(Source, intentLabel, parentIntentId,
            entityCapacity: entities.Length, physicalityCapacity: physicalities.Length,
            attestationCapacity: attestations.Length);
        foreach (var e in entities)      b.AddEntity(e);
        foreach (var p in physicalities) b.AddPhysicality(p);
        foreach (var a in attestations)  b.AddAttestation(a);
        change = b.Build();
        return true;
    }
}
