using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Wiktionary;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class WiktionaryGrammarWitnessTests
{
    [Fact]
    public void JsonLine_ComposesAnd_WitnessAttestsWithoutContentEmitter()
    {
        CodepointPerfcache.LoadDefault();
        LanguageReference.EnsureLoaded();
        const string line = """
        {"word":"filter","lang_code":"en","pos":"noun","senses":[{"glosses":["a device"]}]}
        """;
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(line.Trim());
        var recipe = GrammarDecomposer.LookupById("json");
        using var ast = GrammarDecomposer.Parse(utf8, recipe);
        using var composer = new GrammarRowComposer(utf8, ast, WiktionaryDecomposer.Source, "json");
        var (ents, phys, _, root) = composer.Materialize(0.7);

        var b = new SubstrateChangeBuilder(
            WiktionaryDecomposer.Source, "wiktionary/test/0", null,
            entityCapacity: 64, physicalityCapacity: 64, attestationCapacity: 64);
        foreach (var e in ents) b.AddEntity(e);
        foreach (var p in phys) b.AddPhysicality(p);

        var ctx = new GrammarComposeContext(utf8, ast, root, composer, JsonGrammarHelper.FindRootObjectNode(ast));
        Assert.True(JsonGrammarHelper.TryComposedProperty(ctx, "word", out var wordId), "word must resolve to composed id");
        Assert.NotEqual(default, wordId);
        Assert.Equal(ContentTierSpine.ResolveRoot("filter"), wordId);

        var witness = new WiktionaryGrammarWitness(DecomposerOptions.ForWitness("WiktionaryDecomposer"));
        witness.WalkRow(ctx, new RowContext(0, 1), b);

        var change = b.Build();
        Assert.True(change.Attestations.Length > 0,
            $"expected semantic attestations; ents={change.Entities.Length} phys={change.Physicalities.Length}");
        Assert.Contains(change.Attestations, a => a.TypeId != default);
    }

    [Fact]
    public void SenseLinks_RouteWordNetKey_ToSynsetHub()
    {
        string cili = TestPathHelpers.CiliOrFallback();
        if (!File.Exists(Path.Combine(cili, IliMap.MapFileName)) || IliMap.Load(cili).Count < 100_000)
            return;

        CodepointPerfcache.LoadDefault();
        LanguageReference.EnsureLoaded();

        const string line = """
        {"word":"dog","lang_code":"en","pos":"noun","senses":[{"glosses":["animal"],"links":[["WordNet","30-01313093-n"]]}]}
        """;
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(line.Trim());
        var recipe = GrammarDecomposer.LookupById("json");
        using var ast = GrammarDecomposer.Parse(utf8, recipe);
        using var composer = new GrammarRowComposer(utf8, ast, WiktionaryDecomposer.Source, "json");
        var (ents, phys, _, root) = composer.Materialize(0.7);

        var b = new SubstrateChangeBuilder(
            WiktionaryDecomposer.Source, "wiktionary/synset/0", null,
            entityCapacity: 64, physicalityCapacity: 64, attestationCapacity: 64);
        foreach (var e in ents) b.AddEntity(e);
        foreach (var p in phys) b.AddPhysicality(p);

        var ctx = new GrammarComposeContext(utf8, ast, root, composer, JsonGrammarHelper.FindRootObjectNode(ast));
        Assert.True(JsonGrammarHelper.TryComposedProperty(ctx, "word", out var wordId));

        Hash128? synId = ConceptAnchor.SynsetId(1313093, 'n');
        Assert.NotNull(synId);

        var witness = new WiktionaryGrammarWitness(DecomposerOptions.ForWitness("WiktionaryDecomposer"));
        witness.WalkRow(ctx, new RowContext(0, 1), b);

        var change = b.Build();
        Hash128 correspondsTo = RelationTypeRegistry.Resolve("CORRESPONDS_TO").Id;
        Assert.Contains(change.Attestations, a =>
            a.TypeId == correspondsTo
            && a.SubjectId == wordId
            && a.ObjectId == synId);
    }
}
