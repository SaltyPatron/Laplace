using System.Collections.Immutable;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class GrammarCompositionTests
{
    private static readonly Hash128 Src =
        SubstrateCanonicalIds.OfVersioned("source", "test", "CodeDecomposer");

    private static (List<Hash128> Ents,
                    List<Hash128> Phys,
                    ImmutableArray<AttestationRow> Atts,
                    Hash128 Root) Compose(string text, string modality)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var recipe = GrammarDecomposer.LookupById(modality);
        Assert.NotEqual(IntPtr.Zero, recipe);
        using var ast = GrammarDecomposer.Parse(bytes, recipe);
        using var composer = new GrammarRowComposer(bytes, ast, Src, modality,
            GrammarCompositionMode.FullSource);
        var builder = new SubstrateChangeBuilder(Src, "test/grammar-source");
        Hash128 root = composer.DrainInto(builder, 0.7);
        GrammarTagWitness.Emit(builder, bytes, ast, composer, modality, Src, 0.7);
        var change = builder.Build();
        var entities = CopyTupleParser.ParseEntities(
            change.IntentStages.Select(stage => stage.TupleBuffer(IntentStageTable.Entities)).ToList());
        var physicalities = CopyTupleParser.ParsePhysicalities(
            change.IntentStages.Select(stage => stage.TupleBuffer(IntentStageTable.Physicalities)).ToList());
        return (entities.Ids, physicalities.EntityIds, change.Attestations, root);
    }

    [Fact]
    public void Python_Composes_NonEmpty_And_Deterministic()
    {
        const string src = "def f(x):\n    return x + 1\n";
        var a = Compose(src, "python");
        var b = Compose(src, "python");

        Assert.True(a.Ents.Count > 0, "code file must yield entities");
        Assert.True(a.Phys.Count > 0, "code file must yield physicalities");
        Assert.NotEqual(default, a.Root);

        Assert.Equal(a.Root, b.Root);
        var ids1 = a.Ents.ToHashSet();
        var ids2 = b.Ents.ToHashSet();
        Assert.True(ids1.SetEquals(ids2), "entity ids must be deterministic across runs");
    }

    [Fact]
    public void Python_DefinitionsAndCalls_UseRegisteredTagQueryAndComposedSpans()
    {
        Assert.Contains(typeof(GrammarTags).Assembly.GetManifestResourceNames(),
            name => name.Replace('\\', '/') == "Laplace.GrammarTags.owned.python/queries/tags.scm");
        const string src = "class Greeter:\n    def greet(self):\n        return helper()\n";
        byte[] bytes = Encoding.UTF8.GetBytes(src);
        IntPtr recipe = GrammarDecomposer.LookupById("python");
        Assert.NotEqual(IntPtr.Zero, recipe);
        byte[]? tags = GrammarTags.TagsSource("python");
        Assert.NotNull(tags);
        Assert.NotEmpty(tags!);

        var (_, _, attestations, root) = Compose(src, "python");

        Assert.NotEqual(default, root);
        Assert.Contains(attestations, a => a.TypeId == RelationTypeRegistry.Resolve("DEFINES").Id);
        Assert.Contains(attestations, a => a.TypeId == RelationTypeRegistry.Resolve("CALLS").Id);
    }

    [Fact]
    public void Json_Composes_Through_The_Same_Path()
    {
        var r = Compose("{\"a\": [1, 2], \"b\": true}", "json");
        Assert.True(r.Ents.Count > 0);
        Assert.NotEqual(default, r.Root);
    }

    [Fact]
    public void Identical_Code_Dedups_Within_A_File()
    {

        var once = Compose("x = 1\n", "python");
        var twice = Compose("x = 1\nx = 1\n", "python");

        int distinctOnce = once.Ents.Distinct().Count();
        int distinctTwice = twice.Ents.Distinct().Count();
        Assert.True(distinctTwice <= distinctOnce + 2,
            $"repeated identical code must dedup (once={distinctOnce}, twice={distinctTwice})");
    }

    [Fact]
    public void CodeIdentifier_Reconciles_With_ProseWord()
    {



        var (codeEnts, _, _, _) = Compose("filter\n", "python");

        byte[] prose = Encoding.UTF8.GetBytes("filter");
        Assert.True(TextEntityBuilder.TryBuildRows(prose, Src, out var proseEnts, out _, out _, out _));
        var proseWord = proseEnts.First(e => e.TypeId == TextEntityBuilder.WordTypeId);

        Assert.Contains(proseWord.Id, codeEnts);
    }

    [Fact]
    public void Sql_DefinesAndCalls_EmitAttestations()
    {
        // LANGUAGE sql body so tree-sitter-sql builds an `invocation` node
        // (plpgsql dollar bodies are only partially parsed).
        const string src =
            "CREATE OR REPLACE FUNCTION laplace.foo(x int) RETURNS int LANGUAGE sql AS $$\n" +
            "  SELECT laplace.bar(x);\n" +
            "$$;\n";

        byte[] bytes = Encoding.UTF8.GetBytes(src);
        var recipe = GrammarDecomposer.LookupById("sql");
        Assert.NotEqual(IntPtr.Zero, recipe);
        var tags = GrammarTags.TagsSource("sql");
        Assert.NotNull(tags);
        Assert.True(tags!.Length > 0, "engine/core/grammars/sql/queries/tags.scm must resolve");

        var (_, _, atts, root) = Compose(src, "sql");

        Assert.NotEqual(default, root);
        // DEFINES is an alias of HAS_DEFINITION — Resolve, not RelationTypeId(literal).
        var defines = RelationTypeRegistry.Resolve("DEFINES").Id;
        var calls = RelationTypeRegistry.Resolve("CALLS").Id;
        Assert.Contains(atts, a => a.TypeId == defines);
        Assert.Contains(atts, a => a.TypeId == calls);
    }
}
