using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class CompoundIdentifierDecompositionTests
{
    [Fact]
    public void Roleset_IsCanonicalContent_WithSemanticInterpretation()
    {
        CodepointPerfcache.LoadDefault();
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
        Assert.Equal(ContentEmitter.RootId("abandon.01"), id);
        Assert.True(builder.ContentStage.EntityCount > 0);
        Assert.True(builder.ContentStage.PhysicalityCount > 0);

        var change = builder.Build();
        Assert.Contains(change.Physicalities, p => p.EntityId == id);
        Assert.Contains(change.Attestations, a =>
            a.SubjectId == id
            && a.TypeId == RelationTypeRegistry.RelationTypeId("IS_TYPED_AS")
            && a.ObjectId == EntityTypeRegistry.PropBankRoleset);
    }

    [Fact]
    public void ReferenceDomains_ConvergeOnIdenticalCanonicalContent()
    {
        const string key = "13.1-1";
        Assert.Equal(
            ReferenceAnchor.Id(ReferenceIdentityKind.PropBankRoleset, key),
            ReferenceAnchor.Id(ReferenceIdentityKind.VerbNetClass, key));
        Assert.Equal(
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
    public void WordNetSynsetKey_VersionIsContext_NotASecondContentIdentity()
    {
        Assert.Equal(
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

        Hash128? id = CategoryAnchor.Emit(builder, label, source);

        Assert.Equal(ContentEmitter.RootId(label), id);
        Assert.True(builder.ContentStage.EntityCount > 0);
        Assert.True(builder.ContentStage.PhysicalityCount > 0);
    }
}
