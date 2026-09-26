using System.Text;
using Xunit;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions.Tests;

public class BootstrapIntentBuilderTests
{
    private static readonly Hash128 SourceId =
        SubstrateCanonicalIds.Source("UnicodeDecomposer");
    private static readonly Hash128 TrustClassId =
        TrustClassRegistry.Id("SubstrateMandate");

    // A type or relation key is the content id of its label: the same entity that text
    // is anywhere else.
    private static Hash128 LabelId(string name) => ContentEmitter.RootId(name)!.Value;

    [Fact]
    public void Build_RegistersSourceEntity()
    {
        var b = new BootstrapIntentBuilder(SourceId, "UnicodeDecomposer", TrustClassId);
        var change = b.Build();
        Assert.Contains(change.Entities, e => e.Id == SourceId);
        var srcRow = change.Entities.First(e => e.Id == SourceId);
        Assert.Equal(BootstrapIntentBuilder.SourceTypeId, srcRow.TypeId);
    }

    [Fact]
    public void Build_AddTypeReturnsTheRegistryKeyAndStagesNoEntity()
    {
        var b = new BootstrapIntentBuilder(SourceId, "WordNetDecomposer", TrustClassId);
        var synsetId = b.AddType("WordNet_Synset");
        var senseId = b.AddType("WordNet_Sense");
        var change = b.Build();

        Assert.Equal(LabelId("WordNet_Synset"), synsetId);
        Assert.Equal(LabelId("WordNet_Sense"), senseId);
        Assert.DoesNotContain(change.Entities, e => e.Id == synsetId);
        Assert.DoesNotContain(change.Entities, e => e.Id == senseId);
    }

    [Fact]
    public void Build_AddRelationTypeReturnsTheRegistryKeyAndStagesNoEntity()
    {
        var b = new BootstrapIntentBuilder(SourceId, "WordNetDecomposer", TrustClassId);
        var typeId = b.AddRelationType("HAS_DEFINITION");
        var change = b.Build();
        Assert.Equal(LabelId("HAS_DEFINITION"), typeId);
        Assert.Equal(RelationTypeRegistry.RelationTypeId("HAS_DEFINITION"), typeId);
        Assert.DoesNotContain(change.Entities, e => e.Id == typeId);
    }

    [Fact]
    public void Build_DoesNotTurnGovernedRelationHierarchyIntoVendorTestimony()
    {
        var change = new BootstrapIntentBuilder(
            SourceId, "TestDecomposer", TrustClassId).Build();
        var isA = RelationTypeRegistry.RelationTypeId("IS_A");

        Assert.DoesNotContain(change.Attestations, a => a.TypeId == isA);
        Assert.DoesNotContain(change.Entities,
            e => e.Id == RelationTypeRegistry.RelationTypeId("HAS_DEFINITION"));
    }

    [Fact]
    public void Build_IsDeterministicAcrossRebuilds()
    {
        BootstrapIntentBuilder Make()
        {
            var b = new BootstrapIntentBuilder(SourceId, "DetTest", TrustClassId);
            b.AddType("WordNet_Synset");
            b.AddRelationType("HAS_DEFINITION");
            return b;
        }
        var a = Make().Build();
        var b2 = Make().Build();
        Assert.Equal(a.Metadata.IntentId, b2.Metadata.IntentId);
        Assert.Equal(a.Entities.Length, b2.Entities.Length);
        for (int i = 0; i < a.Entities.Length; i++)
            Assert.Equal(a.Entities[i].Id, b2.Entities[i].Id);
        Assert.Equal(a.Attestations.Select(static x => x.Id), b2.Attestations.Select(static x => x.Id));
    }

    [Fact]
    public void CanonicalIdConventions_AreContentAddressed()
    {
        Assert.Equal(LabelId("Source"), BootstrapIntentBuilder.SourceTypeId);
        Assert.Equal(LabelId("Type"), BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Equal(LabelId("RelationType"), BootstrapIntentBuilder.RelationTypeMetaTypeId);
    }
}

[Collection("GrammarPerfcache")]
public class BootstrapIntentBuilderAliasTests
{
    private static readonly Hash128 TrustClassId =
        TrustClassRegistry.Id("AIModelProbe");

    // A content-hash source (an AI model) must register its own name so realize.render()/
    // realize.label() stop showing raw hex and seed-step verify can resolve name → id
    // through consensus (HAS_NAME {name/primary} → the name's content root == word_id).
    [Fact]
    public void Build_SourceNamesItself_HasNameToContentRoot()
    {
        var contentHashSource = Hash128.Blake3(new byte[] { 1, 2, 3, 4 });
        const string name = "TinyLlama/TinyLlama-1.1B-Chat-v1.0";

        var change = new BootstrapIntentBuilder(contentHashSource, name, TrustClassId).Build();

        var aliasType = RelationTypeRegistry.RelationTypeId("HAS_NAME");
        var alias = Assert.Single(change.Attestations,
            a => a.TypeId == aliasType && a.SubjectId == contentHashSource);
        var expectedRoot = ContentEmitter.RootId(name);
        Assert.NotNull(expectedRoot);
        Assert.Equal(expectedRoot!.Value, alias.ObjectId);
        Assert.Equal(contentHashSource, alias.SourceId);
    }
}
