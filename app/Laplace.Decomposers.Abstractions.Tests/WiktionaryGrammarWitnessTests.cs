using Laplace.Decomposers.Wiktionary;
using Laplace.Decomposers.Abstractions;
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

    var ctx = new GrammarComposeContext(utf8, ast, root, composer);
    Assert.True(JsonGrammarHelper.TryComposedProperty(ctx, "word", out var wordId), "word must resolve to composed id");
    Assert.NotEqual(default, wordId);

    var witness = new WiktionaryGrammarWitness(DecomposerOptions.ForWitness("WiktionaryDecomposer"));
    witness.WalkRow(ctx, new RowContext(0, 1), b);

    var change = b.Build();
    Assert.True(change.Attestations.Length > 0,
        $"expected semantic attestations; ents={change.Entities.Length} phys={change.Physicalities.Length}");
    Assert.Contains(change.Attestations, a => a.TypeId != default);
  }
}
