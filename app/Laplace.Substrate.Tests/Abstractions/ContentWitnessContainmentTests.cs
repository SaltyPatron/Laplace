using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;







[Collection("GrammarPerfcache")]
public sealed class ContentWitnessContainmentTests
{
    private static readonly Hash128 Src =
        SubstrateCanonicalIds.OfVersioned("source", "test", "ContentContainment");

    [Theory]
    [InlineData("dog")]
    [InlineData("hello world")]
    [InlineData("Second sentence, longer this time.")]
    public void PresentBitmap_EmitsZeroEntities(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);

        using var tree = IntentStage.BuildContentTree(bytes);
        Assert.NotNull(tree);

        int n = tree!.NodeCount;

        var present = new byte[(n + 7) / 8];
        for (int i = 0; i < n; i++) present[i >> 3] |= (byte)(1 << (i & 7));

        using var stage = IntentStage.New(256);
        Assert.True(stage.EmitContentTree(tree, Src, present, out var root));
        Assert.NotEqual(default, root);
        Assert.Equal(0, stage.EntityCount);
        Assert.Equal(0, stage.PhysicalityCount);
    }

    [Theory]
    [InlineData("dog")]
    [InlineData("hello world")]
    [InlineData("Second sentence, longer this time.")]
    public void AbsentBitmap_MatchesUnfilteredAdd(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);


        using var baseline = IntentStage.New(256);
        Assert.True(baseline.TryAddContentWitness(bytes, Src, out var baseRoot));


        using var tree = IntentStage.BuildContentTree(bytes);
        Assert.NotNull(tree);
        using var filtered = IntentStage.New(256);
        Assert.True(filtered.EmitContentTree(tree!, Src, ReadOnlySpan<byte>.Empty, out var filtRoot));

        Assert.True(baseRoot.EqualsBytewise(filtRoot),
            $"root diverged for '{s}': baseline={baseRoot} filtered={filtRoot}");
        Assert.Equal(baseline.EntityCount, filtered.EntityCount);
        Assert.Equal(baseline.PhysicalityCount, filtered.PhysicalityCount);
    }

    [Fact]
    public void PresentWord_SkipsOnlyThatSubtree()
    {
        // Grapheme-floor law: single-codepoint clusters are pass-through
        // scaffold and are never emitted, so the smallest emission unit whose
        // presence can shrink the batch is a WORD. Marking "dog" present must
        // skip exactly its subtree while "cat" and the sentence still emit.
        byte[] bytes = Encoding.UTF8.GetBytes("dog cat");

        using var full = IntentStage.New(256);
        Assert.True(full.TryAddContentWitness(bytes, Src, out _));
        int fullEntities = full.EntityCount;

        using var tree = IntentStage.BuildContentTree(bytes);
        Assert.NotNull(tree);
        int n = tree!.NodeCount;


        var bm = new byte[(n + 7) / 8];
        bool marked = false;
        for (uint i = 0; i < (uint)n; i++)
        {
            if (tree.GetNode(i).Tier == 2)
            {
                bm[(int)i >> 3] |= (byte)(1 << ((int)i & 7));
                marked = true;
                break;
            }
        }
        Assert.True(marked, "expected at least one tier-2 word node");

        using var partial = IntentStage.New(256);
        Assert.True(partial.EmitContentTree(tree, Src, bm, out _));

        Assert.True(partial.EntityCount < fullEntities,
            $"partial={partial.EntityCount} should be < full={fullEntities}");
        Assert.True(partial.EntityCount > 0, "a present word must not blank the whole tree");
    }
}
