using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// The collapse rule decides which node is the stored identity: a single-child,
/// span-identical wrapper is its child (one-child composition is the child). It is
/// implemented by <c>TierTree.CollapseIndex</c> and natively by
/// <c>laplace_tier_tree_collapse_index</c> (<see cref="NativeInterop.TierTreeCollapseIndex"/>).
/// Each clause (span check, walk to tier 0, multi-child stop) has its own test, and each
/// asserts the managed and native results agree.
/// </summary>
public class CollapseIndexParityTests
{
    private static uint NativeCollapse(TierTree t, uint idx)
    {
        lock (LaplaceCoreGate.Native)
            return NativeInterop.TierTreeCollapseIndex(t.DangerousGetHandle(), idx);
    }

    /// <summary>tier-0 leaf 'A', wrapped by a span-identical grapheme, wrapped again by a
    /// span-identical word: the tree one-codepoint text composes to.</summary>
    private static TierTree SingleCodepointChain()
    {
        var t = TierTree.New(4);
        t.AddLeaf(0, 65, 0, 1);      // tier 0 codepoint, span [0,1)
        t.AddNode(1, 0, 1, 0, 1);    // tier 1 grapheme, one child, SAME span
        t.AddNode(2, 1, 1, 0, 1);    // tier 2 word, one child, SAME span
        t.FinalizeParents();
        return t;
    }

    [Fact]
    public void Collapse_WalksChainOfSpanIdenticalWrappersToTheTier0Leaf()
    {
        // A single-codepoint word is the codepoint: the walk ends at the tier-0 leaf,
        // not at an intermediate wrapper.
        using var t = SingleCodepointChain();
        Assert.Equal(0u, t.CollapseIndex(2));
        Assert.Equal(0u, t.CollapseIndex(1));
        Assert.Equal(t.CollapseIndex(2), NativeCollapse(t, 2));
        Assert.Equal(t.CollapseIndex(1), NativeCollapse(t, 1));
    }

    [Fact]
    public void Collapse_StopsAtTier0_EvenThoughItIsALeaf()
    {
        using var t = SingleCodepointChain();
        Assert.Equal(0u, t.CollapseIndex(0));
        Assert.Equal(t.CollapseIndex(0), NativeCollapse(t, 0));
    }

    [Fact]
    public void Collapse_DoesNotCollapseMultiChildNodes()
    {
        // Two children means the parent's content is a composition, not a re-wrapping.
        var t = TierTree.New(4);
        t.AddLeaf(0, 65, 0, 1);
        t.AddLeaf(0, 66, 1, 1);
        t.AddNode(1, 0, 2, 0, 2);
        t.FinalizeParents();
        using (t)
        {
            Assert.Equal(2u, t.CollapseIndex(2));
            Assert.Equal(t.CollapseIndex(2), NativeCollapse(t, 2));
        }
    }

    [Fact]
    public void Collapse_DoesNotCollapseWhenTheChildSpanDiffers()
    {
        // One child, but the parent covers more text than the child does, so the parent
        // is not the same content and keeps its own identity.
        var t = TierTree.New(4);
        t.AddLeaf(0, 65, 0, 1);
        t.AddNode(1, 0, 1, 0, 2);   // single child, span [0,2) against the child's [0,1)
        t.FinalizeParents();
        using (t)
        {
            Assert.Equal(1u, t.CollapseIndex(1));
            Assert.Equal(t.CollapseIndex(1), NativeCollapse(t, 1));
        }
    }
}
