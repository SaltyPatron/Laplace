using System.Collections.Immutable;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class GrammarCompositionTests
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/CodeDecomposer/v1");

    private static (ImmutableArray<EntityRow> Ents,
                    ImmutableArray<PhysicalityRow> Phys,
                    ImmutableArray<AttestationRow> Atts,
                    Hash128 Root) Compose(string text, string modality)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var recipe = GrammarDecomposer.LookupById(modality);
        Assert.NotEqual(IntPtr.Zero, recipe);
        using var ast = GrammarDecomposer.Parse(bytes, recipe);
        var geb = new GrammarEntityBuilder(bytes, ast, Src, modality);
        return geb.Build(witnessWeight: 0.7);
    }

    [Fact]
    public void Python_Composes_NonEmpty_And_Deterministic()
    {
        const string src = "def f(x):\n    return x + 1\n";
        var a = Compose(src, "python");
        var b = Compose(src, "python");

        Assert.True(a.Ents.Length > 0, "code file must yield entities");
        Assert.True(a.Phys.Length > 0, "code file must yield physicalities");
        Assert.NotEqual(default, a.Root);

        Assert.Equal(a.Root, b.Root);                       // deterministic root id
        var ids1 = a.Ents.Select(e => e.Id).ToHashSet();
        var ids2 = b.Ents.Select(e => e.Id).ToHashSet();
        Assert.True(ids1.SetEquals(ids2), "entity ids must be deterministic across runs");
    }

    [Fact]
    public void Json_Composes_Through_The_Same_Path()
    {
        var r = Compose("{\"a\": [1, 2], \"b\": true}", "json");
        Assert.True(r.Ents.Length > 0);
        Assert.NotEqual(default, r.Root);
    }

    [Fact]
    public void Identical_Code_Dedups_Within_A_File()
    {
        // two identical statements → the second composes to ids already seen (dedup).
        var once  = Compose("x = 1\n", "python");
        var twice = Compose("x = 1\nx = 1\n", "python");
        // distinct entity ids should NOT double — the repeated statement collapses.
        int distinctOnce  = once.Ents.Select(e => e.Id).Distinct().Count();
        int distinctTwice = twice.Ents.Select(e => e.Id).Distinct().Count();
        Assert.True(distinctTwice <= distinctOnce + 2,
            $"repeated identical code must dedup (once={distinctOnce}, twice={distinctTwice})");
    }

    [Fact]
    public void CodeIdentifier_Reconciles_With_ProseWord()
    {
        // A python identifier 'filter' must compose to the SAME content-addressed id as the
        // prose word 'filter' — both are the merkle over the same grapheme ids (shared floor +
        // shared compose kernel = one entity). This is the one-entity / cross-lingual linchpin.
        var (codeEnts, _, _, _) = Compose("filter\n", "python");

        byte[] prose = Encoding.UTF8.GetBytes("filter");
        Assert.True(TextEntityBuilder.TryBuildRows(prose, Src, out var proseEnts, out _, out _, out _));
        var proseWord = proseEnts.First(e => e.TypeId == TextEntityBuilder.WordTypeId);

        Assert.Contains(codeEnts, e => e.Id == proseWord.Id);
    }
}
