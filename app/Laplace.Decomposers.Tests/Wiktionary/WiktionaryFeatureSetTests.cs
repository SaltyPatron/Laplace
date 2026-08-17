using Laplace.Decomposers.Tests;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Wiktionary;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Tests.Wiktionary;

/// <summary>
/// The Wiktionary write path emits a form's morphological analysis as ONE composition, not as
/// one edge per tag, so the analysis itself has an identity that witnesses can adjudicate.
/// </summary>
public class WiktionaryFeatureSetTests
{
    static WiktionaryFeatureSetTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
    }

    private static readonly Hash128 HasFeature =
        RelationTypeRegistry.RelationTypeId("HAS_FEATURE");
    private static readonly Hash128 TranscribesAs =
        RelationTypeRegistry.RelationTypeId("TRANSCRIBES_AS");

    private static SubstrateChange EmitEntry(WiktionaryEntry e)
    {
        var b = new SubstrateChangeBuilder(
            WiktionaryDecomposer.Source, "wiktionary-feature-set-test");
        WiktionaryEmit.Emit(e, b);
        return b.Build();
    }

    [Fact]
    public void MultiTagFormEmitsOneFeatureEdgeAtASetPhysicality()
    {
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Forms = [new WiktionaryEntry.Form("wolves", ["plural", "nominative", "masculine"])],
        });

        var feature = Assert.Single(change.Attestations, a => a.TypeId == HasFeature);
        Assert.NotNull(feature.ObjectId);

        var bundle = Assert.Single(
            change.Physicalities, p => p.EntityId == feature.ObjectId!.Value);
        Assert.Equal(PhysicalityType.Set, bundle.Type);
        Assert.Equal(3, bundle.NConstituents);
    }

    [Fact]
    public void SameTagsInAnyOrderProduceTheSameFeatureSetId()
    {
        Hash128 Emit(params string[] tags) =>
            Assert.Single(
                EmitEntry(new WiktionaryEntry
                {
                    Word = "wolf",
                    LangCode = "en",
                    Forms = [new WiktionaryEntry.Form("wolves", [.. tags])],
                }).Attestations,
                a => a.TypeId == HasFeature).ObjectId!.Value;

        var forward = Emit("plural", "nominative", "masculine");
        var reversed = Emit("masculine", "nominative", "plural");
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void SingleTagFormStillPointsAtTheTagItself()
    {
        // Tier-floor collapse: a one-member set IS its member, so the degenerate case must not
        // mint a wrapper entity around a single tag.
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Forms = [new WiktionaryEntry.Form("wolves", ["plural"])],
        });

        var feature = Assert.Single(change.Attestations, a => a.TypeId == HasFeature);
        Assert.DoesNotContain(
            change.Physicalities, p => p.Type == PhysicalityType.Set);
        Assert.NotNull(feature.ObjectId);
    }

    [Fact]
    public void UntaggedFormEmitsNoFeatureEdge()
    {
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Forms = [new WiktionaryEntry.Form("wolves", null)],
        });
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == HasFeature);
    }

    [Fact]
    public void EveryDialectTagSurvivesAsTheTranscriptionContext()
    {
        // attestations.context_id is one bytea, so the previous shape kept the first tag and
        // dropped the rest across 5,192,208 TRANSCRIBES_AS rows. A set fits in the slot.
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Sounds = [new WiktionaryEntry.Sound("/wʊlf/", ["US", "UK", "GA"])],
        });

        var sound = Assert.Single(change.Attestations, a => a.TypeId == TranscribesAs);
        Assert.NotNull(sound.ContextId);
        var ctx = Assert.Single(
            change.Physicalities, p => p.EntityId == sound.ContextId!.Value);
        Assert.Equal(PhysicalityType.Set, ctx.Type);
        Assert.Equal(3, ctx.NConstituents);
    }

    private static readonly Hash128 HasUsageRegister =
        RelationTypeRegistry.RelationTypeId("HAS_USAGE_REGISTER");

    [Fact]
    public void MultipleRegisterTagsOnASenseComposeOneReading()
    {
        // "archaic AND humorous" is one register reading, adjudicable as a whole. As separate
        // edges a second witness could confirm "archaic" while refuting the reading it belongs
        // to, and nothing could represent that.
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Senses = [new WiktionaryEntry.Sense { Tags = ["archaic", "humorous", "poetic"] }],
        });

        var reg = Assert.Single(change.Attestations, a => a.TypeId == HasUsageRegister);
        var bundle = Assert.Single(change.Physicalities, p => p.EntityId == reg.ObjectId!.Value);
        Assert.Equal(PhysicalityType.Set, bundle.Type);
        Assert.Equal(3, bundle.NConstituents);
    }

    [Fact]
    public void NonRegisterTagsAreExcludedFromTheReading()
    {
        // Only RegisterTags members participate; a grammatical tag must not join the register
        // set, or the set id stops meaning "this register".
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Senses = [new WiktionaryEntry.Sense { Tags = ["archaic", "transitive", "humorous"] }],
        });

        var reg = Assert.Single(change.Attestations, a => a.TypeId == HasUsageRegister);
        var bundle = Assert.Single(change.Physicalities, p => p.EntityId == reg.ObjectId!.Value);
        Assert.Equal(2, bundle.NConstituents);
    }

    [Fact]
    public void SenseWithNoRegisterTagsEmitsNoRegisterEdge()
    {
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Senses = [new WiktionaryEntry.Sense { Tags = ["transitive", "countable"] }],
        });
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == HasUsageRegister);
    }
}
