using System.Collections.Immutable;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;





public static class UserPromptContent
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/UserPrompt/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/UserPromptContent/v1");

    public static double WitnessWeight =>
        RelationTypeRank.Associative * SourceTrust.UserPrompt;

    public static SubstrateChange BuildBootstrapChange()
    {
        var b = new SubstrateChangeBuilder(Source, "bootstrap/UserPrompt", parentIntentId: null);
        b.AddEntity(Source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId, Source);
        b.AddEntity(TextEntityBuilder.GraphemeTypeId, EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        b.AddEntity(TextEntityBuilder.WordTypeId,     EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        b.AddEntity(TextEntityBuilder.SentenceTypeId, EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        b.AddEntity(TextEntityBuilder.DocumentTypeId, EntityTier.Word, BootstrapIntentBuilder.TypeMetaTypeId, Source);
        return b.Build();
    }

    public static bool TryBuildWitnessChange(
        byte[] utf8,
        string intentLabel,
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

        var b = new SubstrateChangeBuilder(Source, intentLabel, parentIntentId: null,
            entityCapacity: entities.Length, physicalityCapacity: physicalities.Length,
            attestationCapacity: attestations.Length);
        foreach (var e in entities)      b.AddEntity(e);
        foreach (var p in physicalities) b.AddPhysicality(p);
        foreach (var a in attestations)  b.AddAttestation(a);
        change = b.Build();
        return true;
    }

    public static bool TryBuildWitnessRows(
        byte[] utf8,
        out ImmutableArray<EntityRow> entities,
        out ImmutableArray<PhysicalityRow> physicalities,
        out ImmutableArray<AttestationRow> attestations,
        out Hash128 rootId)
        => TextEntityBuilder.TryBuildContentWitness(utf8, Source, WitnessWeight,
            out entities, out physicalities, out attestations, out rootId, out _);
}
