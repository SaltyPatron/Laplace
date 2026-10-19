using Laplace.Decomposers.Tests;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Wiktionary;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Tests.Wiktionary;

/// <summary>
/// A form's tags are membership. Each tag the file lists is its own edge.
/// </summary>
public class WiktionaryFeatureSetTests
{
    static WiktionaryFeatureSetTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
    }

    private static readonly Hash128 Tags = RelationTypeRegistry.RelationTypeId("tags");
    private static readonly Hash128 Ipa = RelationTypeRegistry.RelationTypeId("ipa");

    private static SubstrateChange EmitEntry(WiktionaryEntry e)
    {
        var b = new SubstrateChangeBuilder(
            WiktionaryDecomposer.Source, "wiktionary-feature-set-test");
        WiktionaryEmit.Emit(e, b);
        return b.Build();
    }

    private static HashSet<Hash128> TagObjects(SubstrateChange change, Hash128 subject) =>
        change.Attestations
            .Where(a => a.TypeId == Tags && a.SubjectId == subject && a.ObjectId is not null)
            .Select(a => a.ObjectId!.Value)
            .ToHashSet();

    [Fact]
    public void MultiTagFormEmitsOneEdgePerTag()
    {
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Forms = [new WiktionaryEntry.Form("wolves", ["plural", "nominative", "masculine"])],
        });

        Hash128 form = ContentEmitter.RootId("wolves")!.Value;
        var objects = TagObjects(change, form);
        Assert.Equal(3, objects.Count);
        Assert.Contains(ContentEmitter.RootId("plural")!.Value, objects);
        Assert.Contains(ContentEmitter.RootId("nominative")!.Value, objects);
        Assert.Contains(ContentEmitter.RootId("masculine")!.Value, objects);
        Assert.DoesNotContain(change.Physicalities, p => p.Type == PhysicalityType.Set);
    }

    [Fact]
    public void SameTagsInAnyOrderNameTheSameMembers()
    {
        HashSet<Hash128> Emit(params string[] tags) 
        {
            var change = EmitEntry(new WiktionaryEntry
            {
                Word = "wolf",
                LangCode = "en",
                Forms = [new WiktionaryEntry.Form("wolves", [.. tags])],
            });
            return TagObjects(change, ContentEmitter.RootId("wolves")!.Value);
        }

        Assert.Equal(
            Emit("plural", "nominative", "masculine"),
            Emit("masculine", "nominative", "plural"));
    }

    [Fact]
    public void SingleTagFormPointsAtTheTag()
    {
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Forms = [new WiktionaryEntry.Form("wolves", ["plural"])],
        });

        Hash128 form = ContentEmitter.RootId("wolves")!.Value;
        AttestationRow feature = Assert.Single(change.Attestations, a => a.TypeId == Tags);
        Assert.Equal(form, feature.SubjectId);
        Assert.Equal(ContentEmitter.RootId("plural"), feature.ObjectId);
    }

    [Fact]
    public void UntaggedFormEmitsNoTagEdge()
    {
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Forms = [new WiktionaryEntry.Form("wolves", null)],
        });
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == Tags);
    }

    [Fact]
    public void EveryDialectTagIsMembershipOnTheTranscription()
    {
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Sounds = [new WiktionaryEntry.Sound("/wʊlf/", ["US", "UK", "GA"])],
        });

        AttestationRow sound = Assert.Single(change.Attestations, a => a.TypeId == Ipa);
        Assert.Null(sound.ContextId);
        var tags = TagObjects(change, sound.ObjectId!.Value);
        Assert.Equal(3, tags.Count);
        Assert.Contains(ContentEmitter.RootId("US")!.Value, tags);
        Assert.Contains(ContentEmitter.RootId("UK")!.Value, tags);
        Assert.Contains(ContentEmitter.RootId("GA")!.Value, tags);
    }

    [Fact]
    public void EverySenseTagIsItsOwnMembership()
    {
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Senses = [new WiktionaryEntry.Sense { Tags = ["archaic", "humorous", "poetic"] }],
        });

        var senseEdges = change.Attestations.Where(a => a.TypeId == RelationTypeRegistry.RelationTypeId("senses")).ToArray();
        Hash128 sense = Assert.Single(senseEdges).ObjectId!.Value;
        var tags = TagObjects(change, sense);
        Assert.Equal(3, tags.Count);
        Assert.Contains(ContentEmitter.RootId("archaic")!.Value, tags);
        Assert.Contains(ContentEmitter.RootId("humorous")!.Value, tags);
        Assert.Contains(ContentEmitter.RootId("poetic")!.Value, tags);
    }

    [Fact]
    public void GrammaticalTagsStayInTheSameMembership()
    {
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "wolf",
            LangCode = "en",
            Senses = [new WiktionaryEntry.Sense { Tags = ["archaic", "transitive", "humorous"] }],
        });

        Hash128 sense = Assert.Single(
            change.Attestations, a => a.TypeId == RelationTypeRegistry.RelationTypeId("senses")).ObjectId!.Value;
        var tags = TagObjects(change, sense);
        Assert.Contains(ContentEmitter.RootId("transitive")!.Value, tags);
        Assert.Equal(3, tags.Count);
    }
}