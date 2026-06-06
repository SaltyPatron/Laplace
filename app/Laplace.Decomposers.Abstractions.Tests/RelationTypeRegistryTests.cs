using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The kind registry turns each relation kind into an ARENA (a μ-ranked
/// embedding). These prove the canonicalization rule that makes witnesses
/// co-assert on ONE consensus pk instead of forking parallel near-duplicate
/// arenas: name-normalize (HAS_UPOS→HAS_POS), direction-flip (HAS_HYPONYM→IS_A),
/// symmetric endpoint-order (synonym (a,b)≡(b,a)), and roll-up (nsubj is_a
/// DEPENDS_ON, ATTENDS is_a RELATED_TO). Distinct relations stay distinct.
/// </summary>
public class RelationTypeRegistryTests
{
    private static Hash128 Kid(string n) => RelationTypeRegistry.RelationTypeId(n);

    [Fact]
    public void NameNormalize_PosFamily_OneArena()
    {
        Assert.Equal(Kid("HAS_POS"), RelationTypeRegistry.Resolve("HAS_UPOS").Id);
        Assert.Equal(Kid("HAS_POS"), RelationTypeRegistry.Resolve("HAS_POS").Id);
        // HAS_LEX_CATEGORY is NOT an alias anymore (2026-06-05): a lexname is
        // POS×domain — the WordNet decomposer splits it at ingest. Unregistered
        // name ⇒ probationary self-named arena, never silently folded into POS.
        Assert.NotEqual(Kid("HAS_POS"), RelationTypeRegistry.Resolve("HAS_LEX_CATEGORY").Id);
        Assert.Equal(RelationTypeRank.Probationary, RelationTypeRegistry.Resolve("HAS_LEX_CATEGORY").Rank);
    }

    [Fact]
    public void NameNormalize_DefinitionFamily_OneArena()
    {
        // One assertion, several names (rename table 2026-06-05): the gloss
        // defines the word — HAS_DEFINITION is the canonical attribute form.
        Assert.Equal(Kid("HAS_DEFINITION"), RelationTypeRegistry.Resolve("DEFINES").Id);
        Assert.Equal(Kid("HAS_DEFINITION"), RelationTypeRegistry.Resolve("DEFINED_AS").Id);
        Assert.Equal(Kid("HAS_DEFINITION"), RelationTypeRegistry.Resolve("HAS_DEFINITION").Id);
    }

    [Fact]
    public void NormalizesSplit_TwoAssertions_TwoArenas()
    {
        // The one-name-two-assertions failure class, split 2026-06-05:
        // codepoint→normalized form vs model per-channel norm scale.
        Assert.NotEqual(Kid("NORMALIZES_TO"), Kid("NORM_SCALES"));
        Assert.Equal(RelationTypeRank.StandardsStructural, RelationTypeRegistry.Resolve("NORMALIZES_TO").Rank);
        Assert.Equal(RelationTypeRank.TensorCalculation, RelationTypeRegistry.Resolve("NORM_SCALES").Rank);
        // The retired name resolves probationary — nothing may silently emit it.
        Assert.Equal(RelationTypeRank.Probationary, RelationTypeRegistry.Resolve("NORMALIZES").Rank);
    }

    [Fact]
    public void SymmetricTranslation_BothDirections_OneAttestationId()
    {
        // The Tatoeba fork fix: registry Orient canonicalizes symmetric
        // endpoint order, so (a,b) and (b,a) produce the IDENTICAL evidence id
        // and land on ONE consensus row.
        var src = Hash128.OfCanonical("substrate/test/reg/source");
        var a = Hash128.OfCanonical("substrate/test/reg/a");
        var b = Hash128.OfCanonical("substrate/test/reg/b");
        var ab = RelationTypeRegistry.Attest(a, "IS_TRANSLATION_OF", b, src, SourceTrust.StructuredCorpus);
        var ba = RelationTypeRegistry.Attest(b, "IS_TRANSLATION_OF", a, src, SourceTrust.StructuredCorpus);
        Assert.Equal(ab.Id, ba.Id);
        Assert.Equal(ab.SubjectId, ba.SubjectId);
        Assert.Equal(ab.ObjectId, ba.ObjectId);
    }

    [Fact]
    public void AtomicFamily_RegistryRouted()
    {
        Assert.Equal(RelationTypeRank.Causal, RelationTypeRegistry.Resolve("X_INTENT").Rank);
        // HinderedBy is the SAME assertion as ConceptNet's ObstructedBy (rule 2).
        Assert.Equal(Kid("OBSTRUCTED_BY"), RelationTypeRegistry.Resolve("HINDERED_BY").Id);
        // Convention sweep: family prefix.
        Assert.Equal(Kid("X_FILLED_BY"), RelationTypeRegistry.Resolve("IS_FILLED_BY").Id);
        // MadeUpOf is partitive, rolled up.
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
        // ar_padt lesson (2026-06-05): a treebank may contain advcl:cond and
        // never bare advcl — the parent kind ENTITY must stage with the child
        // or the writer's referential proof fails the batch.
        var b = new SubstrateChangeBuilder(Hash128.OfCanonical("src"), "test/edep", null,
            entityCapacity: 8, physicalityCapacity: 0, attestationCapacity: 8);
        RelationTypeRegistry.SeedEnhancedDeprel(b, "advcl:cond", Hash128.OfCanonical("src"),
            new HashSet<Hash128>(), new ConcurrentIdSet());
        var change = b.Build();
        Assert.Contains(change.Entities, e => e.Id == Kid("EDEP_ADVCL_COND"));
        Assert.Contains(change.Entities, e => e.Id == Kid("EDEP_ADVCL"));
    }

    [Fact]
    public void TensorRoleFamily_FirstClass_NoParent()
    {
        // The model modality is first-class Canon (2026-06-05 ruling): ingest
        // arenas AND the export mold-filling map. Placement arenas — no parent.
        foreach (var role in new[] { "EMBEDS", "Q_PROJECTS", "K_PROJECTS", "V_PROJECTS",
                                     "O_PROJECTS", "GATES", "UP_PROJECTS", "DOWN_PROJECTS",
                                     "NORM_SCALES", "OUTPUT_PROJECTS", "TOKEN_MAPS_TO", "MERGES_WITH" })
        {
            var r = RelationTypeRegistry.Resolve(role);
            Assert.Equal(RelationTypeRank.TensorCalculation, r.Rank);
            Assert.Null(r.ParentId);
            Assert.Equal(RelationTypeRegistry.RelationTypeId(role), r.Id);   // id stability
        }
    }

    [Fact]
    public void DistinctRelations_StayDistinct()
    {
        // Merging any of these would weld two embeddings into one.
        Assert.NotEqual(RelationTypeRegistry.Resolve("IS_SYNONYM_OF").Id, RelationTypeRegistry.Resolve("IS_TRANSLATION_OF").Id);
        Assert.NotEqual(RelationTypeRegistry.Resolve("IS_A").Id,         RelationTypeRegistry.Resolve("HAS_PART").Id);
        Assert.NotEqual(RelationTypeRegistry.Resolve("IS_ANTONYM_OF").Id, RelationTypeRegistry.Resolve("IS_SYNONYM_OF").Id);
    }

    [Fact]
    public void DirectionInverses_CollapseToOneArena_WithFlip()
    {
        var hyper = RelationTypeRegistry.Resolve("HAS_HYPERNYM");   // x is_a y, no flip
        var hypoOf = RelationTypeRegistry.Resolve("IS_HYPERNYM_OF"); // y is_a x, flip
        var hypo = RelationTypeRegistry.Resolve("HAS_HYPONYM");      // y is_a x, flip
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
        var flipped = RelationTypeRegistry.Attest(animal, "HAS_HYPONYM", dog, Hash128.OfCanonical("src"), 1.0);
        var direct  = RelationTypeRegistry.Attest(dog,    "IS_A",        animal, Hash128.OfCanonical("src"), 1.0);
        Assert.Equal(dog, flipped.SubjectId);
        Assert.Equal(animal, flipped.ObjectId);
        Assert.Equal(direct.Id, flipped.Id);   // same content pk → they co-assert
    }

    [Fact]
    public void Symmetric_EndpointOrderCanonicalized_OneRow()
    {
        Hash128 a = Hash128.OfCanonical("e/alpha"), b = Hash128.OfCanonical("e/beta");
        Hash128 src = Hash128.OfCanonical("src");
        var ab = RelationTypeRegistry.Attest(a, "IS_SYNONYM_OF", b, src, 1.0);
        var ba = RelationTypeRegistry.Attest(b, "IS_SYNONYM_OF", a, src, 1.0);
        Assert.Equal(ab.Id, ba.Id);                          // (a,b) ≡ (b,a)
        Assert.Equal(ab.SubjectId, ba.SubjectId);
        Assert.Equal(ab.ObjectId, ba.ObjectId);
    }

    [Fact]
    public void Asymmetric_OrderPreserved()
    {
        Hash128 dog = Hash128.OfCanonical("e/dog"), animal = Hash128.OfCanonical("e/animal");
        Hash128 src = Hash128.OfCanonical("src");
        var fwd = RelationTypeRegistry.Attest(dog, "IS_A", animal, src, 1.0);
        var rev = RelationTypeRegistry.Attest(animal, "IS_A", dog, src, 1.0);
        Assert.NotEqual(fwd.Id, rev.Id);   // dog is_a animal ≠ animal is_a dog
    }

    [Fact]
    public void RollUp_ModelKinds_ParentRelatedTo()
    {
        Assert.Equal(Kid("RELATED_TO"), RelationTypeRegistry.Resolve("ATTENDS").ParentId);
        Assert.Equal(Kid("RELATED_TO"), RelationTypeRegistry.Resolve("OV_RELATES").ParentId);
        Assert.Null(RelationTypeRegistry.Resolve("COMPLETES_TO").ParentId);   // shared w/ corpora, no parent
        Assert.Equal(Kid("HAS_POS"), RelationTypeRegistry.Resolve("HAS_XPOS").ParentId);
    }

    [Fact]
    public void Deprel_DynamicFamily_UnderDependsOn()
    {
        var nsubj = RelationTypeRegistry.ResolveDeprel("nsubj");
        Assert.Equal(Kid("DEP_NSUBJ"), nsubj.Id);
        Assert.Equal(Kid("DEPENDS_ON"), nsubj.ParentId);

        var pass = RelationTypeRegistry.ResolveDeprel("nsubj:pass");   // subtype rolls up to its base
        Assert.Equal(Kid("DEP_NSUBJ_PASS"), pass.Id);
        Assert.Equal(Kid("DEP_NSUBJ"), pass.ParentId);

        // distinct dependency types are distinct arenas (the "more than 37" point)
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
    public void UnknownKind_GracefulProbationaryFallback()
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
}
