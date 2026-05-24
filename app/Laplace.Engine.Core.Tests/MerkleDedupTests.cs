using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

public class MerkleDedupTests
{
    [Fact]
    public void FilterNovel_AllAbsentEmitsAll()
    {
        var ids = new Hash128[]
        {
            new(1, 1), new(2, 2), new(3, 3),
        };
        var bm = new byte[] { 0 };
        var outBuf = new Hash128[3];
        int n = MerkleDedup.FilterNovel(ids, bm, outBuf);
        Assert.Equal(3, n);
        Assert.Equal(ids, outBuf);
    }

    [Fact]
    public void FilterNovel_AllPresentEmitsZero()
    {
        var ids = new Hash128[] { new(1, 1), new(2, 2) };
        var bm = new byte[] { 0xFF };
        var outBuf = new Hash128[2];
        Assert.Equal(0, MerkleDedup.FilterNovel(ids, bm, outBuf));
    }

    [Fact]
    public void FilterNovel_PreservesOrder()
    {
        var ids = new Hash128[]
        {
            new(0, 0), new(1, 1), new(2, 2), new(3, 3),
            new(4, 4), new(5, 5), new(6, 6), new(7, 7),
        };
        // bits set at 0, 2, 5, 6 → novel = {1, 3, 4, 7}
        var bm = new byte[] { 0b01100101 };
        var outBuf = new Hash128[8];
        int n = MerkleDedup.FilterNovel(ids, bm, outBuf);
        Assert.Equal(4, n);
        Assert.Equal(new Hash128(1, 1), outBuf[0]);
        Assert.Equal(new Hash128(3, 3), outBuf[1]);
        Assert.Equal(new Hash128(4, 4), outBuf[2]);
        Assert.Equal(new Hash128(7, 7), outBuf[3]);
    }

    [Fact]
    public void FilterNovel_RejectsUndersizedOutput()
    {
        var ids = new Hash128[] { new(1, 1), new(2, 2) };
        var bm = new byte[] { 0 };
        var outBuf = new Hash128[1];
        Assert.Throws<ArgumentException>(() => MerkleDedup.FilterNovel(ids, bm, outBuf));
    }

    [Fact]
    public void TrunkShortcircuit_AllAbsentEmitsAllIndices()
    {
        using var tree = TierTree.New(8);
        tree.AddLeaf(0, 1, 0, 0);
        tree.AddLeaf(0, 2, 0, 0);
        tree.AddNode(1, 0, 2, 0, 0);
        tree.FinalizeParents();
        var bm = new byte[] { 0 };
        var outBuf = new uint[3];
        int n = MerkleDedup.TrunkShortcircuit(tree, bm, outBuf);
        Assert.Equal(3, n);
        Assert.Equal(0u, outBuf[0]);
        Assert.Equal(1u, outBuf[1]);
        Assert.Equal(2u, outBuf[2]);
    }

    [Fact]
    public void TrunkShortcircuit_RootPresentEmitsNothing()
    {
        using var tree = TierTree.New(8);
        tree.AddLeaf(0, 1, 0, 0);
        tree.AddLeaf(0, 2, 0, 0);
        tree.AddNode(1, 0, 2, 0, 0);  // root at idx 2
        tree.FinalizeParents();
        var bm = new byte[] { 0b00000100 }; // bit 2 set
        var outBuf = new uint[3];
        Assert.Equal(0, MerkleDedup.TrunkShortcircuit(tree, bm, outBuf));
    }
}
