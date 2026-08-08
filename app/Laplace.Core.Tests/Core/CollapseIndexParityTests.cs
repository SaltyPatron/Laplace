using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// GH #904 item 2. The tier-floor collapse rule exists twice — <c>TierTree.CollapseIndex</c>
/// and <c>collapse_idx()</c> in <c>engine/core/src/content_witness_batch.c</c> — and it
/// decides WHICH NODE IS THE STORED IDENTITY: a single-child, span-identical wrapper is
/// its child, same bytes and same id. The C# comment said "keep the two in lockstep",
/// which is prose, not a gate; a drift on either side changes which entity gets minted
/// for content that has not changed.
///
/// These pin every clause of the rule separately, so a partial edit (dropping the span
/// check, stopping at tier 1, collapsing multi-child nodes) fails a specific test rather
/// than being absorbed. The C twin is <c>laplace_tier_tree_collapse_index</c> via
/// <see cref="NativeInterop.TierTreeCollapseIndex"/>; each clause also asserts C# == C.
/// </summary>
public class CollapseIndexParityTests
{
    private static uint NativeCollapse(TierTree t, uint idx)
    {
        lock (LaplaceCoreGate.Native)
            return NativeInterop.TierTreeCollapseIndex(t.DangerousGetHandle(), idx);
    }

    /// <summary>tier-0 leaf 'A', wrapped by a span-identical grapheme, wrapped again by a
    /// span-identical word: the chain the ingest actually produces for one-codepoint text.</summary>
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
        // The whole point: a single-codepoint word IS the codepoint. Stopping anywhere
        // above leaf 0 mints a tier-1 or tier-2 entity row carrying the codepoint's id
        // with the wrong stored tier — the exact defect the `tier <= 1` stop caused.
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
        // is NOT the same content and must keep its own identity. Dropping this clause
        // is the subtle half of the rule and the easiest to lose in a refactor.
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
