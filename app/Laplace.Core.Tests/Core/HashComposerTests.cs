using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

public class HashComposerTests
{
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe int SynthResolver(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(atom & 0xFF);
        bytes[1] = (byte)((atom >> 8) & 0xFF);
        bytes[2] = (byte)((atom >> 16) & 0xFF);
        bytes[3] = (byte)((atom >> 24) & 0xFF);
        *outId = Hash128.Blake3(bytes);
        outCoord[0] = atom / 1000.0;
        outCoord[1] = 0.0; outCoord[2] = 0.0; outCoord[3] = 0.0;
        Span<double> c = stackalloc double[4] { outCoord[0], 0, 0, 0 };
        *outHb = Hilbert128.Encode(c);
        return 0;
    }

    [Fact]
    public void Run_PopulatesLeafFromResolver()
    {
        using var t = TierTree.New(1);
        t.AddLeaf(0, 42, 0, 0);
        unsafe { HashComposer.Run(t, &SynthResolver); }
        var v = t.GetNode(0);
        var expectedId = Hash128.Blake3(new byte[] { 42, 0, 0, 0 });
        Assert.Equal(expectedId, v.Id);
        unsafe { Assert.Equal(0.042, v.Coord[0]); }
    }

    [Fact]
    public void Run_InteriorIdIsMerkleOfChildren()
    {
        using var t = TierTree.New(8);
        t.AddLeaf(0, 100, 0, 1);
        t.AddLeaf(0, 101, 1, 1);
        t.AddNode(1, 0, 2, 0, 2);
        unsafe { HashComposer.Run(t, &SynthResolver); }

        var leaf0 = Hash128.Blake3(new byte[] { 100, 0, 0, 0 });
        var leaf1 = Hash128.Blake3(new byte[] { 101, 0, 0, 0 });
        var expected = Hash128.Merkle(1, new[] { leaf0, leaf1 });
        Assert.Equal(expected, t.GetNode(2).Id);
    }

    [Fact]
    public void Run_DeterministicAcrossTwoSeparatelyBuiltTrees()
    {
        TierTree Build()
        {
            var t = TierTree.New(4);
            t.AddLeaf(0, 200, 0, 1);
            t.AddLeaf(0, 201, 1, 1);
            t.AddNode(1, 0, 2, 0, 2);
            return t;
        }
        using var a = Build();
        using var b = Build();
        unsafe
        {
            HashComposer.Run(a, &SynthResolver);
            HashComposer.Run(b, &SynthResolver);
        }
        for (uint i = 0; i < 3; i++)
        {
            Assert.Equal(a.GetNode(i).Id, b.GetNode(i).Id);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int ErrResolver(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
        => -7;

    [Fact]
    public void Run_PropagatesResolverError()
    {
        using var t = TierTree.New(1);
        t.AddLeaf(0, 1, 0, 0);
        unsafe
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                HashComposer.Run(t, &ErrResolver));
            Assert.Contains("-7", ex.Message);
        }
    }
}
