using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Compound identifiers staged as content (PropBank roleset keys like
/// "abandon.01", VerbNet class ids, FrameNet frame names — everything
/// CategoryAnchor.Emit / ContentEmitter touches) MUST decompose through the
/// same tiered content pipeline as ordinary text. The load-bearing
/// consequence of content addressing is that the "abandon" inside
/// "abandon.01" is THE SAME ENTITY (bit-identical id) as the "abandon"
/// WordNet ingested — merging happens by hash collision, not by an
/// entity-resolution pass. If these tests fail, category anchors are
/// minting opaque word-tier blobs that never link to anything: orphaned
/// clutter that no recall/walk can reach from the lexical graph.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class CompoundIdentifierDecompositionTests
{
    private static HashSet<Hash128> NodeIds(string text, out int nodeCount, out int maxTier)
    {
        using var tree = IntentStage.BuildContentTree(Encoding.UTF8.GetBytes(text));
        Assert.NotNull(tree);
        nodeCount = tree!.NodeCount;
        var ids = new HashSet<Hash128>();
        maxTier = 0;
        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var node = tree.GetNode(i);
            ids.Add(node.Id);
            maxTier = Math.Max(maxTier, node.Tier);
        }
        return ids;
    }

    private static HashSet<Hash128> TierIds(string text, int tier)
    {
        using var tree = IntentStage.BuildContentTree(Encoding.UTF8.GetBytes(text));
        Assert.NotNull(tree);
        var ids = new HashSet<Hash128>();
        for (uint i = 0; i < tree!.NodeCount; i++)
        {
            var node = tree.GetNode(i);
            if (node.Tier == tier) ids.Add(node.Id);
        }
        return ids;
    }

    [Fact]
    public void RolesetKey_DecomposesIntoConstituents_NotOneOpaqueBlob()
    {
        var ids = NodeIds("abandon.01", out int nodeCount, out int maxTier);
        // 1 root + word nodes + grapheme nodes + shared codepoint nodes — an
        // opaque single-entity encoding would have a handful of nodes at one
        // tier. "abandon.01" must fan out across tiers.
        Assert.True(nodeCount > 10,
            $"expected full tiered decomposition, got {nodeCount} nodes");
        Assert.True(maxTier >= 2, $"expected word tier or above, got maxTier={maxTier}");
        Assert.True(ids.Count > 10);
    }

    [Fact]
    public void CompoundSharesWordEntity_WithStandaloneWord()
    {
        // The word-tier ids inside "abandon.01" must include the word-tier
        // id of standalone "abandon" — same content, same hash, same entity.
        var compoundWords = TierIds("abandon.01", tier: 2);
        var standaloneWords = TierIds("abandon", tier: 2);

        Assert.NotEmpty(standaloneWords);
        Assert.True(standaloneWords.IsSubsetOf(compoundWords),
            "the 'abandon' inside 'abandon.01' must be bit-identical to standalone 'abandon' — " +
            "otherwise category anchors mint orphaned blobs that never merge with lexical content");
    }

    [Fact]
    public void CompoundSharesGraphemesAndCodepoints_WithStandaloneWord()
    {
        static Dictionary<int, HashSet<Hash128>> PerTier(string text)
        {
            using var tree = IntentStage.BuildContentTree(Encoding.UTF8.GetBytes(text));
            Assert.NotNull(tree);
            var byTier = new Dictionary<int, HashSet<Hash128>>();
            for (uint i = 0; i < tree!.NodeCount; i++)
            {
                var node = tree.GetNode(i);
                if (!byTier.TryGetValue(node.Tier, out var set))
                    byTier[node.Tier] = set = new HashSet<Hash128>();
                set.Add(node.Id);
            }
            return byTier;
        }

        var compound = PerTier("abandon.01");
        var standalone = PerTier("abandon");
        var compoundAll = compound.Values.SelectMany(s => s).ToHashSet();

        // Every SUB-word tier of standalone "abandon" (words, graphemes,
        // codepoints — tiers <= 2) must reappear identically inside the
        // compound's tree; only wrapper tiers above the word (sentence/
        // document nodes whose content legitimately differs) may diverge.
        var leaks = new List<string>();
        foreach (var (tier, ids) in standalone.OrderBy(kv => kv.Key))
        {
            if (tier > 2) continue;
            int missing = ids.Count(id => !compoundAll.Contains(id));
            if (missing > 0) leaks.Add($"tier {tier}: {missing}/{ids.Count} distinct ids missing");
        }
        Assert.True(leaks.Count == 0,
            "constituents of standalone 'abandon' missing from 'abandon.01' — " + string.Join("; ", leaks));
    }

    [Fact]
    public void VnClassAndFrameNames_AlsoDecompose()
    {
        // The other identifier families staged by SemLink/VerbNet/FrameNet
        // witnesses: numeric VN class ids and CamelCase frame names.
        var vn = NodeIds("13.1-1", out int vnCount, out _);
        Assert.True(vnCount > 5, $"VN class id must decompose, got {vnCount} nodes");

        var fn = NodeIds("Abandonment", out int fnCount, out int fnMaxTier);
        Assert.True(fnCount > 5, $"frame name must decompose, got {fnCount} nodes");
        Assert.True(fnMaxTier >= 2);

        Assert.True(vn.Count > 0 && fn.Count > 0);
    }
}
