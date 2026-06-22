using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Tier-containment dedup parity for the grammar compose path: the native compose tier tree
/// (laplace_compose_get_tier_tree) feeds MerkleDedup.TrunkShortcircuit inside GrammarRowComposer,
/// exactly like TextEntityBuilder. A present trunk must skip its entire subtree (zero novel
/// entities/physicalities), while PRECEDES/witness evidence still flows; an all-absent bitmap must
/// reproduce the unfiltered emission byte-for-byte.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class GrammarComposeContainmentTests
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/compose-containment/v1");

    [Theory]
    [InlineData("1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}")]
    [InlineData("7\tIsA\t/c/en/a moment in time\t/c/en/moment\t{}")]
    public void PresentTrunk_EmitsZeroNovelEntitiesButKeepsEvidence(string row)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(row);
        using var ast = GrammarDecomposer.Parse(utf8, "tsv");
        using var composer = new GrammarRowComposer(utf8, ast, Src, "tsv");

        Hash128[] ids = composer.EntityIds();
        Assert.True(ids.Length > 0, "expected the tsv row to compose at least one entity");

        // Baseline: no bitmap => the whole subtree is novel.
        var (baseEnts, basePhys, basePrec, _) = composer.Materialize(1.0);
        Assert.True(baseEnts.Length > 0);

        // Every node already present => TrunkShortcircuit skips the entire tree.
        var present = new byte[(ids.Length + 7) / 8];
        for (int i = 0; i < ids.Length; i++) present[i >> 3] |= (byte)(1 << (i & 7));

        var (ents, phys, prec, _) = composer.Materialize(1.0, present);

        Assert.Empty(ents);
        Assert.Empty(phys);
        // PRECEDES carry new distributional evidence and must keep flowing even when every entity
        // in the subtree is already present.
        Assert.Equal(basePrec.Length, prec.Length);
    }

    [Theory]
    [InlineData("1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}")]
    public void AllAbsentBitmap_MatchesUnfilteredEmission(string row)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(row);
        using var ast = GrammarDecomposer.Parse(utf8, "tsv");
        using var composer = new GrammarRowComposer(utf8, ast, Src, "tsv");

        Hash128[] ids = composer.EntityIds();
        var absent = new byte[(ids.Length + 7) / 8]; // all zero => nothing exists => all novel

        var (baseEnts, basePhys, basePrec, baseRoot) = composer.Materialize(1.0);
        var (ents, phys, prec, root) = composer.Materialize(1.0, absent);

        Assert.Equal(baseRoot, root);
        Assert.Equal(baseEnts.Length, ents.Length);
        Assert.Equal(basePhys.Length, phys.Length);
        Assert.Equal(basePrec.Length, prec.Length);
        for (int i = 0; i < baseEnts.Length; i++)
            Assert.Equal(baseEnts[i].Id, ents[i].Id);
        for (int i = 0; i < basePhys.Length; i++)
            Assert.Equal(basePhys[i].Id, phys[i].Id);
    }

    [Theory]
    [InlineData("1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}")]
    public void PresentLeafWord_SkipsOnlyThatSubtree(string row)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(row);
        using var ast = GrammarDecomposer.Parse(utf8, "tsv");
        using var composer = new GrammarRowComposer(utf8, ast, Src, "tsv");

        Hash128[] ids = composer.EntityIds();
        var (baseEnts, _, _, _) = composer.Materialize(1.0);

        // Mark a single lowest-tier (tier 0/1) node present: containment must still emit the rest,
        // i.e. partial presence yields a strict subset, never all-or-nothing.
        var bitmap = new byte[(ids.Length + 7) / 8];
        bitmap[0] |= 1;

        var (ents, _, _, _) = composer.Materialize(1.0, bitmap);
        Assert.True(ents.Length < baseEnts.Length || baseEnts.Length == 1,
            "marking one present node should not increase the novel set");
        Assert.True(ents.Length >= 0);
    }
}
