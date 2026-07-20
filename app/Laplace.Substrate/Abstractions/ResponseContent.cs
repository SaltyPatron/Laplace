using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;








public static class ResponseContent
{
    public static readonly Hash128 Source =
        SubstrateCanonicalIds.Source("Response");
    public static readonly Hash128 TrustClass =
        SubstrateCanonicalIds.TrustClass("ResponseContent");

    public static double WitnessWeight =>
        RelationTypeRank.Associative * SourceTrust.Response;

    public static SubstrateChange BuildBootstrapChange()
    {
        var b = new SubstrateChangeBuilder(Source, "bootstrap/Response", parentIntentId: null);
        b.AddEntity(Source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId, Source);
        b.AddEntity(TextEntityBuilder.GraphemeTypeId, EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        b.AddEntity(TextEntityBuilder.WordTypeId, EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        b.AddEntity(TextEntityBuilder.SentenceTypeId, EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        b.AddEntity(TextEntityBuilder.DocumentTypeId, EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        return b.Build();
    }



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
        foreach (var e in entities) b.AddEntity(e);
        foreach (var p in physicalities) b.AddPhysicality(p);
        foreach (var a in attestations) b.AddAttestation(a);
        change = b.Build();
        return true;
    }
}
