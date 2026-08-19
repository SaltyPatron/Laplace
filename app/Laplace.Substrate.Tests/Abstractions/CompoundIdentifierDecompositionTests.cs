using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class CompoundIdentifierDecompositionTests
{
    [Fact]
    public void OpaqueRoleset_IsOneGovernedIdentity_NotAContentTree()
    {
        var source = SubstrateCanonicalIds.Source("reference-admission-test");
        var builder = new SubstrateChangeBuilder(source, "propbank/abandon.01");

        Hash128? id = ReferenceAnchor.Emit(
            builder,
            ReferenceIdentityKind.PropBankRoleset,
            "abandon.01",
            EntityTypeRegistry.PropBankRoleset,
            source,
            SourceTrust.AcademicCurated);

        Assert.NotNull(id);
        Assert.Equal(0, builder.ContentStage.EntityCount);
        var change = builder.Build();
        var entity = Assert.Single(change.Entities);
        Assert.Equal(id, entity.Id);
        Assert.Equal(EntityTypeRegistry.PropBankRoleset, entity.TypeId);
        Assert.Empty(change.Physicalities);
        Assert.False(EntityIdentityPolicy.RequiresPhysicality(entity.TypeId));
    }

    [Fact]
    public void ReferenceDomains_KeepIdenticalSerializationsDistinct()
    {
        const string key = "13.1-1";
        Assert.NotEqual(
            ReferenceAnchor.Id(ReferenceIdentityKind.PropBankRoleset, key),
            ReferenceAnchor.Id(ReferenceIdentityKind.VerbNetClass, key));
        Assert.NotEqual(
            ReferenceAnchor.Id(ReferenceIdentityKind.VerbNetClass, key),
            ContentEmitter.RootId(key));
    }

    [Fact]
    public void ReferenceNormalization_TrimsWithoutChangingCaseOrMeaning()
    {
        Assert.Equal(
            ReferenceAnchor.Id(ReferenceIdentityKind.VerbNetClass, "13.1-1"),
            ReferenceAnchor.Id(ReferenceIdentityKind.VerbNetClass, " 13.1-1 "));
        Assert.NotEqual(
            ReferenceAnchor.Id(ReferenceIdentityKind.VerbNetClass, "Giving"),
            ReferenceAnchor.Id(ReferenceIdentityKind.VerbNetClass, "giving"));
    }

    [Fact]
    public void WordNetSynsetKey_IsVersionScopedInPropositionIdentity()
    {
        Assert.NotEqual(
            ReferenceAnchor.WordNetSynsetKeyId("pwn30", "02084071-n"),
            ReferenceAnchor.WordNetSynsetKeyId("pwn16", "02084071-n"));
    }

    [Fact]
    public void HumanReadableFrameLabel_RemainsContentAddressed()
    {
        CodepointPerfcache.LoadDefault();
        var source = SubstrateCanonicalIds.Source("content-category-test");
        string label = "Giving_" + Guid.NewGuid().ToString("N");
        var builder = new SubstrateChangeBuilder(source, "framenet/content-label");

        Hash128? id = CategoryAnchor.Emit(
            builder, label, EntityTypeRegistry.FrameNetFrame,
            source, SourceTrust.AcademicCurated);

        Assert.Equal(ContentEmitter.RootId(label), id);
        Assert.True(builder.ContentStage.EntityCount > 0);
        Assert.True(builder.ContentStage.PhysicalityCount > 0);
    }
}
