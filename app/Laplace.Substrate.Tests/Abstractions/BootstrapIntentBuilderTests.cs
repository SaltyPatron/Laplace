using System.Text;
using Xunit;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions.Tests;

public class BootstrapIntentBuilderTests
{
    private static readonly Hash128 SourceId =
        Hash128.OfCanonical("substrate/source/UnicodeDecomposer/v1");
    private static readonly Hash128 TrustClassId =
        Hash128.OfCanonical("substrate/trust_class/SubstrateMandate/v1");

    private static Hash128 TypeHash(string name) =>
        Hash128.Blake3(Encoding.UTF8.GetBytes(name));

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
    public void Build_AddTypeRegistersTypeEntityWithStableId()
    {
        var b = new BootstrapIntentBuilder(SourceId, "WordNetDecomposer", TrustClassId);
        var synsetId = b.AddType("WordNet_Synset");
        var senseId = b.AddType("WordNet_Sense");
        var change = b.Build();

        Assert.Equal(TypeHash("WordNet_Synset"), synsetId);
        Assert.Equal(TypeHash("WordNet_Sense"), senseId);
        Assert.Contains(change.Entities, e => e.Id == synsetId);
        Assert.Contains(change.Entities, e => e.Id == senseId);
    }

    [Fact]
    public void Build_AddRelationTypeRegistersEntityWithStableId()
    {
        var b = new BootstrapIntentBuilder(SourceId, "WordNetDecomposer", TrustClassId);
        var typeId = b.AddRelationType("HAS_DEFINITION");
        var change = b.Build();
        Assert.Equal(TypeHash("HAS_DEFINITION"), typeId);
        Assert.Contains(change.Entities, e => e.Id == typeId);
    }

    [Fact]
    public void Build_EmitsHasTrustClassAttestation()
    {
        var b = new BootstrapIntentBuilder(SourceId, "TestDecomposer", TrustClassId);
        var change = b.Build();
        var a = Assert.Single(change.Attestations,
            x => x.TypeId == BootstrapIntentBuilder.HasTrustClassTypeId);
        Assert.Equal(SourceId, a.SubjectId);
        Assert.Equal(TrustClassId, a.ObjectId);
        Assert.Equal(SourceId, a.SourceId);
        Assert.Null(a.ContextId);
    }

    [Fact]
    public void Build_IsDeterministicAcrossRebuilds()
    {
        BootstrapIntentBuilder Make()
        {
            var b = new BootstrapIntentBuilder(SourceId, "DetTest", TrustClassId);
            b.AddType("DetTest_Foo");
            b.AddRelationType("DET_TEST_TYPE");
            return b;
        }
        var a = Make().Build();
        var b2 = Make().Build();
        Assert.Equal(a.Metadata.IntentId, b2.Metadata.IntentId);
        Assert.Equal(a.Entities.Length, b2.Entities.Length);
        for (int i = 0; i < a.Entities.Length; i++)
            Assert.Equal(a.Entities[i].Id, b2.Entities[i].Id);
    }

    [Fact]
    public void CanonicalIdConventions_AreContentAddressed()
    {
        Assert.Equal(TypeHash("Source"), BootstrapIntentBuilder.SourceTypeId);
        Assert.Equal(TypeHash("Type"), BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Equal(TypeHash("RelationType"), BootstrapIntentBuilder.RelationTypeMetaTypeId);
        Assert.Equal(TypeHash("HAS_TRUST_CLASS"), BootstrapIntentBuilder.HasTrustClassTypeId);
    }
}

[Collection("GrammarPerfcache")]
public class BootstrapIntentBuilderAliasTests
{
    private static readonly Hash128 TrustClassId =
        Hash128.OfCanonical("substrate/trust_class/AIModelProbe/v1");

    // A content-hash source (an AI model) must register its own name so render()/
    // label() stop showing raw hex and seed-step verify can resolve name → id
    // through consensus (HAS_NAME_ALIAS → the name's content root == word_id).
    [Fact]
    public void Build_SourceNamesItself_HasNameAliasToContentRoot()
    {
        var contentHashSource = Hash128.Blake3(new byte[] { 1, 2, 3, 4 });
        const string name = "TinyLlama/TinyLlama-1.1B-Chat-v1.0";

        var change = new BootstrapIntentBuilder(contentHashSource, name, TrustClassId).Build();

        var aliasType = RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS");
        var alias = Assert.Single(change.Attestations,
            a => a.TypeId == aliasType && a.SubjectId == contentHashSource);
        var expectedRoot = ContentEmitter.RootId(name);
        Assert.NotNull(expectedRoot);
        Assert.Equal(expectedRoot!.Value, alias.ObjectId);
        Assert.Equal(contentHashSource, alias.SourceId);
    }
}
