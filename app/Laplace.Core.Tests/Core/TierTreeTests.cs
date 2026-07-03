using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

public class TierTreeTests
{
    private static TierTree BuildSample()
    {
        var t = TierTree.New(8);
        Assert.Equal(0u, t.AddLeaf(0, 100, 0, 1));
        Assert.Equal(1u, t.AddLeaf(0, 101, 1, 1));
        Assert.Equal(2u, t.AddLeaf(0, 102, 2, 1));
        Assert.Equal(3u, t.AddLeaf(0, 103, 3, 1));
        Assert.Equal(4u, t.AddNode(1, 0, 2, 0, 2));
        Assert.Equal(5u, t.AddNode(1, 2, 2, 2, 2));
        Assert.Equal(6u, t.AddNode(2, 4, 2, 0, 4));
        t.FinalizeParents();
        return t;
    }

    [Fact]
    public void New_WithZeroCapacityIsValid()
    {
        using var t = TierTree.New(0);
        Assert.Equal(0, t.NodeCount);
    }

    [Fact]
    public void New_NegativeCapacityThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TierTree.New(-1));
    }

    [Fact]
    public void AddLeaf_ReturnsSequentialIndices()
    {
        using var t = TierTree.New(4);
        Assert.Equal(0u, t.AddLeaf(0, 65, 0, 1));
        Assert.Equal(1u, t.AddLeaf(0, 66, 1, 1));
        Assert.Equal(2u, t.AddLeaf(0, 67, 2, 1));
        Assert.Equal(3, t.NodeCount);
    }

    [Fact]
    public void AddNode_OutOfRangeChildrenThrows()
    {
        using var t = TierTree.New(4);
        Assert.Throws<InvalidOperationException>(() => t.AddNode(1, 0, 2, 0, 0));
    }

    [Fact]
    public void FinalizeParents_PopulatesParentIdxCorrectly()
    {
        using var t = BuildSample();
        Assert.Equal(4u, t.GetNode(0).ParentIdx);
        Assert.Equal(4u, t.GetNode(1).ParentIdx);
        Assert.Equal(5u, t.GetNode(2).ParentIdx);
        Assert.Equal(5u, t.GetNode(3).ParentIdx);
        Assert.Equal(6u, t.GetNode(4).ParentIdx);
        Assert.Equal(6u, t.GetNode(5).ParentIdx);
        Assert.Equal(TierTree.Invalid, t.GetNode(6).ParentIdx);
    }

    [Fact]
    public void FinalizeParents_IsIdempotent()
    {
        using var t = BuildSample();
        t.FinalizeParents();
        Assert.Equal(4u, t.GetNode(0).ParentIdx);
    }

    [Fact]
    public void GetNode_OutOfBoundsThrows()
    {
        using var t = TierTree.New(2);
        t.AddLeaf(0, 1, 0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => t.GetNode(1));
    }

    [Fact]
    public void GetNode_PreComposeIdAndCoordAreZero()
    {
        using var t = BuildSample();
        for (uint i = 0; i <= 6; i++)
        {
            var v = t.GetNode(i);
            Assert.Equal(Hash128.Zero, v.Id);
            unsafe { Assert.Equal(0.0, v.Coord[0]); Assert.Equal(0.0, v.Coord[3]); }
        }
    }

    [Fact]
    public void SetIdSetCoordSetHilbert_RoundTrip()
    {
        using var t = TierTree.New(1);
        t.AddLeaf(0, 1, 0, 0);
        var id = new Hash128(0x0102030405060708ul, 0x090A0B0C0D0E0F10ul);
        t.SetId(0, id);
        Span<double> c = stackalloc double[] { 0.1, 0.2, 0.3, 0.4 };
        t.SetCoord(0, c);
        var hb = Hilbert128.Encode(c);
        t.SetHilbert(0, hb);

        var v = t.GetNode(0);
        Assert.Equal(id, v.Id);
        unsafe
        {
            Assert.Equal(0.1, v.Coord[0]);
            Assert.Equal(0.4, v.Coord[3]);
            for (int i = 0; i < 16; i++) Assert.Equal(hb.Bytes[i], v.Hilbert.Bytes[i]);
        }
    }

    [Fact]
    public void DisposeFreesEngineHandle()
    {
        var t = TierTree.New(1);
        t.AddLeaf(0, 1, 0, 0);
        t.Dispose();
        Assert.Throws<ObjectDisposedException>(() => t.NodeCount);
    }
}
