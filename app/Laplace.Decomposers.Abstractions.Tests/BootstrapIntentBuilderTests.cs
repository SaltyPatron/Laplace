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

    [Fact]
    public void Build_RegistersSourceEntity()
    {
        var b = new BootstrapIntentBuilder(SourceId, "UnicodeDecomposer", TrustClassId);
        var change = b.Build();
        // First entity is the source itself
        Assert.Contains(change.Entities, e => e.Id == SourceId);
        // Source's type_id is the canonical Source type
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

        Assert.Equal(Hash128.OfCanonical("substrate/type/WordNet_Synset/v1"), synsetId);
        Assert.Equal(Hash128.OfCanonical("substrate/type/WordNet_Sense/v1"), senseId);
        Assert.Contains(change.Entities, e => e.Id == synsetId);
        Assert.Contains(change.Entities, e => e.Id == senseId);
    }

    [Fact]
    public void Build_AddKindRegistersKindEntityWithStableId()
    {
        var b = new BootstrapIntentBuilder(SourceId, "WordNetDecomposer", TrustClassId);
        var kindId = b.AddKind("IS_HYPERNYM_OF");
        var change = b.Build();
        Assert.Equal(Hash128.OfCanonical("substrate/kind/IS_HYPERNYM_OF/v1"), kindId);
        Assert.Contains(change.Entities, e => e.Id == kindId);
    }

    [Fact]
    public void Build_EmitsHasTrustClassAttestation()
    {
        var b = new BootstrapIntentBuilder(SourceId, "TestDecomposer", TrustClassId);
        var change = b.Build();
        // There's exactly one attestation; it's the trust-class one
        Assert.Single(change.Attestations);
        var a = change.Attestations[0];
        Assert.Equal(SourceId, a.SubjectId);
        Assert.Equal(BootstrapIntentBuilder.HasTrustClassKindId, a.KindId);
        Assert.Equal(TrustClassId, a.ObjectId);
        Assert.Equal(SourceId, a.SourceId);
        Assert.Null(a.ContextId);
    }

    [Fact]
    public void Build_IsDeterministicAcrossRebuilds()
    {
        BootstrapIntentBuilder Make() {
            var b = new BootstrapIntentBuilder(SourceId, "DetTest", TrustClassId);
            b.AddType("DetTest_Foo");
            b.AddKind("DET_TEST_KIND");
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
    public void CanonicalIdConventions_AreStable()
    {
        // These are load-bearing — every decomposer derives type/kind IDs
        // from BLAKE3 of this canonical-name convention; tests pin the
        // convention so cross-decomposer entity convergence works.
        Assert.Equal(Hash128.OfCanonical("substrate/type/Source/v1"),
                     BootstrapIntentBuilder.SourceTypeId);
        Assert.Equal(Hash128.OfCanonical("substrate/type/Type/v1"),
                     BootstrapIntentBuilder.TypeMetaTypeId);
        Assert.Equal(Hash128.OfCanonical("substrate/type/Kind/v1"),
                     BootstrapIntentBuilder.KindMetaTypeId);
        Assert.Equal(Hash128.OfCanonical("substrate/kind/HAS_TRUST_CLASS/v1"),
                     BootstrapIntentBuilder.HasTrustClassKindId);
    }
}
