using System.Text;
using Laplace.Decomposers.Abstractions;
using System.Linq;
using Laplace.Decomposers.Wiktionary;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Tests.Wiktionary;

public sealed class WiktionarySenseIdentityTests
{
    static WiktionarySenseIdentityTests()
    {
        if (!CodepointPerfcache.IsLoaded) CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());
        LanguageReference.EnsureLoaded(TestIngestPaths.Iso639);
    }

    private static readonly Hash128 HasSense = RelationTypeRegistry.RelationTypeId("HAS_SENSE");
    private static readonly Hash128 HasDefinition = RelationTypeRegistry.RelationTypeId("HAS_DEFINITION");
    private static readonly Hash128 HasExample = RelationTypeRegistry.RelationTypeId("HAS_EXAMPLE");
    private static readonly Hash128 IsSynonymOf = RelationTypeRegistry.RelationTypeId("IS_SYNONYM_OF");
    private static readonly Hash128 CorrespondsTo = RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO");

    private static SubstrateChange EmitEntry(WiktionaryEntry entry)
    {
        var builder = new SubstrateChangeBuilder(
            WiktionaryDecomposer.Source, "wiktionary-sense-identity-test");
        WiktionaryEmit.Emit(entry, builder);
        return builder.Build();
    }

    [Fact]
    public void CachedRelationIdsFollowTheDeclaredSourceRoster()
    {
        Assert.Equal(RelationTypeRegistry.RelationTypeId("HAS_LANGUAGE"),
            WiktionarySource.HasLanguageTypeId);
        Assert.Equal(RelationTypeRegistry.RelationTypeId("CORRESPONDS_TO"),
            WiktionarySource.CorrespondsToTypeId);
        Assert.Equal(RelationTypeRegistry.RelationTypeId("HAS_SENSE"),
            WiktionarySource.HasSenseTypeId);
        Assert.Equal(RelationTypeRegistry.RelationTypeId("IS_SENSE_OF"),
            WiktionarySource.IsSenseOfTypeId);
        Assert.Equal(RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS"),
            WiktionarySource.HasNameAliasTypeId);
    }

    [Fact]
    public void ParserKeepsSenseIdsAndWikidataIdsIndependent()
    {
        const string json = """
        {"word":"bank","lang_code":"en","pos":"noun","senses":[{
          "glosses":["a financial institution"],
          "senseid":["en-bank-en-noun-1","en-bank-en-noun-2"],
          "wikidata":["Q22687","Q19707"]
        }]}
        """;

        WiktionaryEntry entry = Assert.IsType<WiktionaryEntry>(WiktionaryEntry.Parse(
            Encoding.UTF8.GetBytes(json), DecomposerOptions.ForWitness("WiktionaryDecomposer")));
        WiktionaryEntry.Sense sense = Assert.Single(entry.Senses!);
        Assert.Equal(["en-bank-en-noun-1", "en-bank-en-noun-2"], sense.SenseIds);
        Assert.Equal(["Q22687", "Q19707"], sense.WikidataIds);
    }

    [Fact]
    public void SenseScopedFactsRemainOnTheirOwnSense()
    {
        const string financeDefinition = "an institution that handles money";
        const string riverDefinition = "sloping land beside a river";
        const string financeExample = "She deposited money at the bank.";
        const string riverExample = "They sat on the river bank.";
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "bank",
            LangCode = "en",
            Pos = "noun",
            Senses =
            [
                new WiktionaryEntry.Sense
                {
                    SenseIds = ["en-bank-noun-finance"],
                    Glosses = [financeDefinition],
                    Examples = [financeExample],
                    Relations = new WiktionaryEntry.RelationBlock { Synonyms = [new WiktionaryMember("depository", 0.0)] },
                },
                new WiktionaryEntry.Sense
                {
                    SenseIds = ["en-bank-noun-river"],
                    Glosses = [riverDefinition],
                    Examples = [riverExample],
                    Relations = new WiktionaryEntry.RelationBlock { Synonyms = [new WiktionaryMember("riverbank", 0.0)] },
                },
            ],
        });

        Hash128 word = ContentTierSpine.ResolveRoot("bank")!.Value;
        Hash128 financeGloss = ContentTierSpine.ResolveRoot(financeDefinition)!.Value;
        Hash128 riverGloss = ContentTierSpine.ResolveRoot(riverDefinition)!.Value;
        Hash128 financeEx = ContentTierSpine.ResolveRoot(financeExample)!.Value;
        Hash128 riverEx = ContentTierSpine.ResolveRoot(riverExample)!.Value;

        AttestationRow[] memberships = change.Attestations
            .Where(a => a.SubjectId == word && a.TypeId == HasSense).ToArray();
        Assert.Equal(2, memberships.Length);
        Hash128 financeSense = Assert.Single(change.Attestations,
            a => a.TypeId == HasDefinition && a.ObjectId == financeGloss).SubjectId;
        Hash128 riverSense = Assert.Single(change.Attestations,
            a => a.TypeId == HasDefinition && a.ObjectId == riverGloss).SubjectId;

        Assert.NotEqual(financeSense, riverSense);
        Assert.Equal(EntityTypeRegistry.WiktionarySense,
            Assert.Single(change.Entities, e => e.Id == financeSense).TypeId);
        Assert.Equal(EntityTypeRegistry.WiktionarySense,
            Assert.Single(change.Entities, e => e.Id == riverSense).TypeId);
        Assert.DoesNotContain(change.Physicalities,
            p => p.EntityId == financeSense || p.EntityId == riverSense);
        Assert.False(EntityIdentityPolicy.RequiresPhysicality(EntityTypeRegistry.WiktionarySense));
        Assert.Contains(memberships, a => a.ObjectId == financeSense);
        Assert.Contains(memberships, a => a.ObjectId == riverSense);
        Assert.Contains(change.Attestations,
            a => a.SubjectId == financeSense && a.TypeId == HasExample && a.ObjectId == financeEx);
        Assert.Contains(change.Attestations,
            a => a.SubjectId == riverSense && a.TypeId == HasExample && a.ObjectId == riverEx);
        Assert.DoesNotContain(change.Attestations,
            a => a.SubjectId == financeSense && a.TypeId == HasExample && a.ObjectId == riverEx);
        Assert.DoesNotContain(change.Attestations,
            a => a.SubjectId == riverSense && a.TypeId == HasExample && a.ObjectId == financeEx);
        Assert.DoesNotContain(change.Attestations,
            a => a.SubjectId == word && (a.TypeId == HasDefinition || a.TypeId == HasExample));
        Assert.Equal(2, change.Attestations.Count(a => a.TypeId == IsSynonymOf));
        Assert.All(change.Attestations.Where(a => a.TypeId == IsSynonymOf),
            a => Assert.True(
                a.SubjectId == financeSense || a.SubjectId == riverSense
                || a.ObjectId == financeSense || a.ObjectId == riverSense));
    }

    [Fact]
    public void UnorderedSensePayloadHasStableIdentity()
    {
        Hash128 Emit(List<string> glosses, List<string> tags, List<string> synonyms)
        {
            var change = EmitEntry(new WiktionaryEntry
            {
                Word = "fork",
                LangCode = "en",
                Pos = "noun",
                Senses = [new WiktionaryEntry.Sense
                {
                    Glosses = glosses,
                    Tags = tags,
                    Relations = new WiktionaryEntry.RelationBlock {
                        Synonyms = synonyms?.Select(x => new WiktionaryMember(x, 0.0)).ToList() },
                }],
            });
            return Assert.Single(change.Attestations, a => a.TypeId == HasSense).ObjectId!.Value;
        }

        Hash128 forward = Emit(["a pronged utensil", "a tool for eating"],
            ["countable", "common"], ["eating utensil", "tableware"]);
        Hash128 reversed = Emit(["a tool for eating", "a pronged utensil"],
            ["common", "countable"], ["tableware", "eating utensil"]);
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void SourceSenseIdKeepsIdentityStableAcrossPayloadRefresh()
    {
        Hash128 Emit(string gloss) => Assert.Single(
            EmitEntry(new WiktionaryEntry
            {
                Word = "bank",
                LangCode = "en",
                Pos = "noun",
                Senses = [new WiktionaryEntry.Sense
                {
                    SenseIds = ["en-bank-noun-finance"],
                    Glosses = [gloss],
                }],
            }).Attestations,
            a => a.TypeId == HasSense).ObjectId!.Value;

        Assert.Equal(Emit("a financial institution"), Emit("a business that handles money"));
    }

    [Fact]
    public void LanguagePosAndSurfaceArePartOfSenseIdentityNotOnlyContext()
    {
        var sense = new WiktionaryEntry.Sense { SenseIds = ["sense-1"] };
        Hash128 bank = ContentTierSpine.ResolveRoot("bank")!.Value;
        Hash128 shore = ContentTierSpine.ResolveRoot("shore")!.Value;
        Hash128 en = LanguageReference.Resolve("en");
        Hash128 fr = LanguageReference.Resolve("fr");
        Hash128 noun = PosReference.Resolve("noun", PosReference.PosTagset.Wiktionary);
        Hash128 verb = PosReference.Resolve("verb", PosReference.PosTagset.Wiktionary);

        Hash128 baseline = WiktionarySenseAnchor.Id(bank, en, noun, sense)!.Value;
        Assert.NotEqual(baseline, WiktionarySenseAnchor.Id(bank, fr, noun, sense));
        Assert.NotEqual(baseline, WiktionarySenseAnchor.Id(bank, en, verb, sense));
        Assert.NotEqual(baseline, WiktionarySenseAnchor.Id(shore, en, noun, sense));
    }

    [Fact]
    public void WikidataQidIsAGovernedReferenceNotTextContent()
    {
        var change = EmitEntry(new WiktionaryEntry
        {
            Word = "bank",
            LangCode = "en",
            Pos = "noun",
            Senses = [new WiktionaryEntry.Sense
            {
                SenseIds = ["en-bank-noun-finance"],
                Glosses = ["a financial institution"],
                WikidataIds = ["q22687"],
            }],
        });

        Hash128 item = ReferenceAnchor.Id(ReferenceIdentityKind.WikidataItem, "Q22687")!.Value;
        EntityRow entity = Assert.Single(change.Entities, e => e.Id == item);
        Assert.Equal(EntityTypeRegistry.WikidataItem, entity.TypeId);
        Assert.False(EntityIdentityPolicy.RequiresPhysicality(EntityTypeRegistry.WikidataItem));
        Assert.DoesNotContain(change.Physicalities, p => p.EntityId == item);
        Hash128 sense = Assert.Single(change.Attestations, a => a.TypeId == HasSense).ObjectId!.Value;
        Assert.Contains(change.Attestations, a =>
            a.TypeId == CorrespondsTo
            && ((a.SubjectId == sense && a.ObjectId == item)
                || (a.SubjectId == item && a.ObjectId == sense)));
    }
}
