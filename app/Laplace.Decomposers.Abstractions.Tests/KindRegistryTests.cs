using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The kind registry turns each relation kind into an ARENA (a μ-ranked
/// embedding). These prove the canonicalization rule that makes witnesses
/// co-assert on ONE consensus pk instead of forking parallel near-duplicate
/// arenas: name-normalize (HAS_UPOS→HAS_POS), direction-flip (HAS_HYPONYM→IS_A),
/// symmetric endpoint-order (synonym (a,b)≡(b,a)), and roll-up (nsubj is_a
/// DEPENDS_ON, ATTENDS is_a RELATED_TO). Distinct relations stay distinct.
/// </summary>
public class KindRegistryTests
{
    private static Hash128 Kid(string n) => KindRegistry.KindId(n);

    [Fact]
    public void NameNormalize_PosFamily_OneArena()
    {
        Assert.Equal(Kid("HAS_POS"), KindRegistry.Resolve("HAS_UPOS").Id);
        Assert.Equal(Kid("HAS_POS"), KindRegistry.Resolve("HAS_LEX_CATEGORY").Id);
        Assert.Equal(Kid("HAS_POS"), KindRegistry.Resolve("HAS_POS").Id);
    }

    [Fact]
    public void DistinctRelations_StayDistinct()
    {
        // Merging any of these would weld two embeddings into one.
        Assert.NotEqual(KindRegistry.Resolve("IS_SYNONYM_OF").Id, KindRegistry.Resolve("IS_TRANSLATION_OF").Id);
        Assert.NotEqual(KindRegistry.Resolve("IS_A").Id,         KindRegistry.Resolve("HAS_PART").Id);
        Assert.NotEqual(KindRegistry.Resolve("IS_ANTONYM_OF").Id, KindRegistry.Resolve("IS_SYNONYM_OF").Id);
    }

    [Fact]
    public void DirectionInverses_CollapseToOneArena_WithFlip()
    {
        var hyper = KindRegistry.Resolve("HAS_HYPERNYM");   // x is_a y, no flip
        var hypoOf = KindRegistry.Resolve("IS_HYPERNYM_OF"); // y is_a x, flip
        var hypo = KindRegistry.Resolve("HAS_HYPONYM");      // y is_a x, flip
        Assert.Equal(Kid("IS_A"), hyper.Id);
        Assert.Equal(Kid("IS_A"), hypoOf.Id);
        Assert.Equal(Kid("IS_A"), hypo.Id);
        Assert.False(hyper.Flip);
        Assert.True(hypoOf.Flip);
        Assert.True(hypo.Flip);
    }

    [Fact]
    public void Flip_AppliedToEndpoints_OnAttest()
    {
        // HAS_HYPONYM(animal, dog): animal's hyponym is dog ⇒ canonical dog IS_A animal.
        Hash128 animal = Hash128.OfCanonical("e/animal"), dog = Hash128.OfCanonical("e/dog");
        var flipped = KindRegistry.Attest(animal, "HAS_HYPONYM", dog, Hash128.OfCanonical("src"), 1.0);
        var direct  = KindRegistry.Attest(dog,    "IS_A",        animal, Hash128.OfCanonical("src"), 1.0);
        Assert.Equal(dog, flipped.SubjectId);
        Assert.Equal(animal, flipped.ObjectId);
        Assert.Equal(direct.Id, flipped.Id);   // same content pk → they co-assert
    }

    [Fact]
    public void Symmetric_EndpointOrderCanonicalized_OneRow()
    {
        Hash128 a = Hash128.OfCanonical("e/alpha"), b = Hash128.OfCanonical("e/beta");
        Hash128 src = Hash128.OfCanonical("src");
        var ab = KindRegistry.Attest(a, "IS_SYNONYM_OF", b, src, 1.0);
        var ba = KindRegistry.Attest(b, "IS_SYNONYM_OF", a, src, 1.0);
        Assert.Equal(ab.Id, ba.Id);                          // (a,b) ≡ (b,a)
        Assert.Equal(ab.SubjectId, ba.SubjectId);
        Assert.Equal(ab.ObjectId, ba.ObjectId);
    }

    [Fact]
    public void Asymmetric_OrderPreserved()
    {
        Hash128 dog = Hash128.OfCanonical("e/dog"), animal = Hash128.OfCanonical("e/animal");
        Hash128 src = Hash128.OfCanonical("src");
        var fwd = KindRegistry.Attest(dog, "IS_A", animal, src, 1.0);
        var rev = KindRegistry.Attest(animal, "IS_A", dog, src, 1.0);
        Assert.NotEqual(fwd.Id, rev.Id);   // dog is_a animal ≠ animal is_a dog
    }

    [Fact]
    public void RollUp_ModelKinds_ParentRelatedTo()
    {
        Assert.Equal(Kid("RELATED_TO"), KindRegistry.Resolve("ATTENDS").ParentId);
        Assert.Equal(Kid("RELATED_TO"), KindRegistry.Resolve("OV_RELATES").ParentId);
        Assert.Null(KindRegistry.Resolve("COMPLETES_TO").ParentId);   // shared w/ corpora, no parent
        Assert.Equal(Kid("HAS_POS"), KindRegistry.Resolve("HAS_XPOS").ParentId);
    }

    [Fact]
    public void Deprel_DynamicFamily_UnderDependsOn()
    {
        var nsubj = KindRegistry.ResolveDeprel("nsubj");
        Assert.Equal(Kid("DEP_NSUBJ"), nsubj.Id);
        Assert.Equal(Kid("DEPENDS_ON"), nsubj.ParentId);

        var pass = KindRegistry.ResolveDeprel("nsubj:pass");   // subtype rolls up to its base
        Assert.Equal(Kid("DEP_NSUBJ_PASS"), pass.Id);
        Assert.Equal(Kid("DEP_NSUBJ"), pass.ParentId);

        // distinct dependency types are distinct arenas (the "more than 37" point)
        Assert.NotEqual(KindRegistry.ResolveDeprel("nsubj").Id, KindRegistry.ResolveDeprel("obj").Id);
    }

    [Fact]
    public void Feature_DynamicFamily_PerType_UnderHasFeature()
    {
        Assert.True(KindRegistry.ParseFeature("Number=Sing", out var n, out var v));
        Assert.Equal("Number", n);
        Assert.Equal("Sing", v);

        var num = KindRegistry.ResolveFeature("Number");
        Assert.Equal(Kid("FEAT_NUMBER"), num.Id);
        Assert.Equal(Kid("HAS_FEATURE"), num.ParentId);
        Assert.NotEqual(KindRegistry.ResolveFeature("Number").Id, KindRegistry.ResolveFeature("Case").Id);
    }

    [Fact]
    public void UnknownKind_GracefulProbationaryFallback()
    {
        var r = KindRegistry.Resolve("ZZZ_NOT_REGISTERED");
        Assert.Equal(Kid("ZZZ_NOT_REGISTERED"), r.Id);
        Assert.Equal(KindRank.Probationary, r.Rank);
        Assert.Null(r.ParentId);
    }

    [Fact]
    public void Rank_FromScale_NotInline()
    {
        Assert.Equal(KindRank.Taxonomic, KindRegistry.Resolve("IS_A").Rank);
        Assert.Equal(KindRank.Associative, KindRegistry.Resolve("RELATED_TO").Rank);
        Assert.Equal(KindRank.TensorCalculation, KindRegistry.Resolve("ATTENDS").Rank);
    }
}
