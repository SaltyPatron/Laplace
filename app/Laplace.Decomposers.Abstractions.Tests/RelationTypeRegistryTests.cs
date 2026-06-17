using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions.Tests;

public class RelationTypeRegistryTests
{
    private static Hash128 Kid(string n) => RelationTypeRegistry.RelationTypeId(n);

    [Fact]
    public void NameNormalize_PosFamily_OneArena()
    {
        Assert.Equal(Kid("HAS_POS"), RelationTypeRegistry.Resolve("HAS_UPOS").Id);
        Assert.Equal(Kid("HAS_POS"), RelationTypeRegistry.Resolve("HAS_POS").Id);
        Assert.NotEqual(Kid("HAS_POS"), RelationTypeRegistry.Resolve("HAS_LEX_CATEGORY").Id);
        Assert.Equal(RelationTypeRank.Probationary, RelationTypeRegistry.Resolve("HAS_LEX_CATEGORY").Rank);
    }

    [Fact]
    public void NameNormalize_DefinitionFamily_OneArena()
    {
        Assert.Equal(Kid("HAS_DEFINITION"), RelationTypeRegistry.Resolve("DEFINES").Id);
        Assert.Equal(Kid("HAS_DEFINITION"), RelationTypeRegistry.Resolve("DEFINED_AS").Id);
        Assert.Equal(Kid("HAS_DEFINITION"), RelationTypeRegistry.Resolve("HAS_DEFINITION").Id);
    }

    [Fact]
    public void NormalizesSplit_TwoAssertions_TwoArenas()
    {
        Assert.NotEqual(Kid("NORMALIZES_TO"), Kid("NORMALIZES"));
        Assert.Equal(RelationTypeRank.StandardsStructural, RelationTypeRegistry.Resolve("NORMALIZES_TO").Rank);
        Assert.Equal(RelationTypeRank.Probationary, RelationTypeRegistry.Resolve("NORMALIZES").Rank);
    }

    [Fact]
    public void SymmetricTranslation_BothDirections_OneAttestationId()
    {
        var src = Hash128.OfCanonical("substrate/test/reg/source");
        var a = Hash128.OfCanonical("substrate/test/reg/a");
        var b = Hash128.OfCanonical("substrate/test/reg/b");
        var ab = NativeAttestation.Categorical(a, "IS_TRANSLATION_OF", b, src, null, SourceTrust.StructuredCorpus);
        var ba = NativeAttestation.Categorical(b, "IS_TRANSLATION_OF", a, src, null, SourceTrust.StructuredCorpus);
        Assert.Equal(ab.Id, ba.Id);
        Assert.Equal(ab.SubjectId, ba.SubjectId);
        Assert.Equal(ab.ObjectId, ba.ObjectId);
    }

    [Fact]
    public void AtomicFamily_RegistryRouted()
    {
        Assert.Equal(RelationTypeRank.Causal, RelationTypeRegistry.Resolve("X_INTENT").Rank);
        Assert.Equal(Kid("OBSTRUCTED_BY"), RelationTypeRegistry.Resolve("HINDERED_BY").Id);
        Assert.Equal(Kid("X_FILLED_BY"), RelationTypeRegistry.Resolve("IS_FILLED_BY").Id);
        Assert.Equal(Kid("HAS_PART"), RelationTypeRegistry.Resolve("MADE_UP_OF").ParentId);
    }

    [Fact]
    public void Adjacency_OneArena_FollowsIsTheFlip()
    {
        var r = RelationTypeRegistry.Resolve("FOLLOWS");
        Assert.Equal(Kid("PRECEDES"), r.Id);
        Assert.True(r.Flip);
    }

    [Fact]
    public void SeedEnhancedDeprel_SubtypedRel_StagesParentChain()
    {
        var b = new SubstrateChangeBuilder(Hash128.OfCanonical("src"), "test/edep", null,
            entityCapacity: 8, physicalityCapacity: 0, attestationCapacity: 8);
        RelationTypeRegistry.SeedEnhancedDeprel(b, "advcl:cond", Hash128.OfCanonical("src"),
            new HashSet<Hash128>(), new ConcurrentIdSet());
        var change = b.Build();
        Assert.Contains(change.Entities, e => e.Id == Kid("EDEP_ADVCL_COND"));
        Assert.Contains(change.Entities, e => e.Id == Kid("EDEP_ADVCL"));
    }

    [Fact]
    public void TokenizerWitnessTypes_FirstClass_NoParent()
    {
        foreach (var role in new[] { "TOKEN_MAPS_TO", "MERGES_WITH" })
        {
            var r = RelationTypeRegistry.Resolve(role);
            Assert.Equal(RelationTypeRank.TensorCalculation, r.Rank);
            Assert.Null(r.ParentId);
            Assert.Equal(RelationTypeRegistry.RelationTypeId(role), r.Id);
        }
    }

    
    
    
    [Fact]
    public void TensorRoleArenas_Purged_FallToProbationary()
    {
        foreach (var dead in new[] { "EMBEDS", "Q_PROJECTS", "K_PROJECTS", "V_PROJECTS",
                                     "O_PROJECTS", "GATES", "UP_PROJECTS", "DOWN_PROJECTS",
                                     "NORM_SCALES", "OUTPUT_PROJECTS", "DETECTS", "WRITES" })
            Assert.Equal(RelationTypeRank.Probationary, RelationTypeRegistry.Resolve(dead).Rank);
    }

    [Fact]
    public void DistinctRelations_StayDistinct()
    {
        Assert.NotEqual(RelationTypeRegistry.Resolve("IS_SYNONYM_OF").Id, RelationTypeRegistry.Resolve("IS_TRANSLATION_OF").Id);
        Assert.NotEqual(RelationTypeRegistry.Resolve("IS_A").Id,         RelationTypeRegistry.Resolve("HAS_PART").Id);
        Assert.NotEqual(RelationTypeRegistry.Resolve("IS_ANTONYM_OF").Id, RelationTypeRegistry.Resolve("IS_SYNONYM_OF").Id);
    }

    [Fact]
    public void DirectionInverses_CollapseToOneArena_WithFlip()
    {
        var hyper = RelationTypeRegistry.Resolve("HAS_HYPERNYM");
        var hypoOf = RelationTypeRegistry.Resolve("IS_HYPERNYM_OF");
        var hypo = RelationTypeRegistry.Resolve("HAS_HYPONYM");
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
        Hash128 animal = Hash128.OfCanonical("e/animal"), dog = Hash128.OfCanonical("e/dog");
        var flipped = NativeAttestation.Categorical(animal, "HAS_HYPONYM", dog, Hash128.OfCanonical("src"), null, 1.0);
        var direct  = NativeAttestation.Categorical(dog,    "IS_A",        animal, Hash128.OfCanonical("src"), null, 1.0);
        Assert.Equal(dog, flipped.SubjectId);
        Assert.Equal(animal, flipped.ObjectId);
        Assert.Equal(direct.Id, flipped.Id);
    }

    [Fact]
    public void Symmetric_EndpointOrderCanonicalized_OneRow()
    {
        Hash128 a = Hash128.OfCanonical("e/alpha"), b = Hash128.OfCanonical("e/beta");
        Hash128 src = Hash128.OfCanonical("src");
        var ab = NativeAttestation.Categorical(a, "IS_SYNONYM_OF", b, src, null, 1.0);
        var ba = NativeAttestation.Categorical(b, "IS_SYNONYM_OF", a, src, null, 1.0);
        Assert.Equal(ab.Id, ba.Id);
        Assert.Equal(ab.SubjectId, ba.SubjectId);
        Assert.Equal(ab.ObjectId, ba.ObjectId);
    }

    [Fact]
    public void Asymmetric_OrderPreserved()
    {
        Hash128 dog = Hash128.OfCanonical("e/dog"), animal = Hash128.OfCanonical("e/animal");
        Hash128 src = Hash128.OfCanonical("src");
        var fwd = NativeAttestation.Categorical(dog, "IS_A", animal, src, null, 1.0);
        var rev = NativeAttestation.Categorical(animal, "IS_A", dog, src, null, 1.0);
        Assert.NotEqual(fwd.Id, rev.Id);
    }

    [Fact]
    public void RollUp_ModelRelationTypes_ParentRelatedTo()
    {
        Assert.Equal(Kid("RELATED_TO"), RelationTypeRegistry.Resolve("ATTENDS").ParentId);
        Assert.Equal(Kid("RELATED_TO"), RelationTypeRegistry.Resolve("OV_RELATES").ParentId);
        Assert.Null(RelationTypeRegistry.Resolve("COMPLETES_TO").ParentId);
        Assert.Equal(Kid("HAS_POS"), RelationTypeRegistry.Resolve("HAS_XPOS").ParentId);
    }

    [Fact]
    public void Deprel_DynamicFamily_UnderDependsOn()
    {
        var nsubj = RelationTypeRegistry.ResolveDeprel("nsubj");
        Assert.Equal(Kid("DEP_NSUBJ"), nsubj.Id);
        Assert.Equal(Kid("DEPENDS_ON"), nsubj.ParentId);

        var pass = RelationTypeRegistry.ResolveDeprel("nsubj:pass");
        Assert.Equal(Kid("DEP_NSUBJ_PASS"), pass.Id);
        Assert.Equal(Kid("DEP_NSUBJ"), pass.ParentId);

        Assert.NotEqual(RelationTypeRegistry.ResolveDeprel("nsubj").Id, RelationTypeRegistry.ResolveDeprel("obj").Id);
    }

    [Fact]
    public void Feature_DynamicFamily_PerType_UnderHasFeature()
    {
        Assert.True(RelationTypeRegistry.ParseFeature("Number=Sing", out var n, out var v));
        Assert.Equal("Number", n);
        Assert.Equal("Sing", v);

        var num = RelationTypeRegistry.ResolveFeature("Number");
        Assert.Equal(Kid("FEAT_NUMBER"), num.Id);
        Assert.Equal(Kid("HAS_FEATURE"), num.ParentId);
        Assert.NotEqual(RelationTypeRegistry.ResolveFeature("Number").Id, RelationTypeRegistry.ResolveFeature("Case").Id);
    }

    [Fact]
    public void UnknownRelationType_GracefulProbationaryFallback()
    {
        var r = RelationTypeRegistry.Resolve("ZZZ_NOT_REGISTERED");
        Assert.Equal(Kid("ZZZ_NOT_REGISTERED"), r.Id);
        Assert.Equal(RelationTypeRank.Probationary, r.Rank);
        Assert.Null(r.ParentId);
    }

    [Fact]
    public void Rank_FromScale_NotInline()
    {
        Assert.Equal(RelationTypeRank.Taxonomic, RelationTypeRegistry.Resolve("IS_A").Rank);
        Assert.Equal(RelationTypeRank.Associative, RelationTypeRegistry.Resolve("RELATED_TO").Rank);
        Assert.Equal(RelationTypeRank.TensorCalculation, RelationTypeRegistry.Resolve("ATTENDS").Rank);
    }

    [Fact]
    public void AllCanonical_EnumeratesNativeManifest()
    {
        var all = RelationTypeRegistry.AllCanonical().ToList();
        Assert.True(all.Count >= 100);
        Assert.Contains(all, r => r.Canonical == "PRECEDES");
        Assert.DoesNotContain(all, r => r.Canonical == "HAS_UPOS");
    }
}
