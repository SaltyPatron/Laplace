using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// Exercises the engine text-decomposition path, which (post-decouple) reads
/// segmentation + NFC properties from the runtime-loaded perf-cache — hence
/// the <see cref="PerfcacheTestFixture"/> via the "Perfcache" collection.
/// </summary>
[Collection("Perfcache")]
public class TextDecomposerTests
{
    public TextDecomposerTests(PerfcacheTestFixture _) { }


    [Fact]
    public void EmptyString_ProducesRootOnly()
    {
        using var t = TextDecomposer.Run("");
        Assert.Equal(1, t.NodeCount);
        var root = t.GetNode(0);
        Assert.Equal(4, root.Tier);
    }

    [Fact]
    public void NullStringThrows()
    {
        Assert.Throws<ArgumentNullException>(() => TextDecomposer.Run((string)null!));
    }

    [Fact]
    public void InvalidUtf8Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TextDecomposer.Run(new byte[] { 0xFF, 0xFE }));
    }

    [Fact]
    public void HelloWorld_HasReasonableTopology()
    {
        using var t = TextDecomposer.Run("Hello world.");
        int leaves = 0, graphemes = 0, words = 0, sentences = 0, docs = 0;
        for (uint i = 0; i < (uint)t.NodeCount; i++)
        {
            var v = t.GetNode(i);
            switch (v.Tier)
            {
                case 0: leaves++; break;
                case 1: graphemes++; break;
                case 2: words++; break;
                case 3: sentences++; break;
                case 4: docs++; break;
            }
        }
        Assert.Equal(12, leaves);
        Assert.Equal(12, graphemes);
        Assert.True(words >= 1);
        Assert.True(sentences >= 1);
        Assert.Equal(1, docs);
    }

    [Fact]
    public void DeterministicAcrossRuns()
    {
        const string s = "The quick brown fox.";
        using var a = TextDecomposer.Run(s);
        using var b = TextDecomposer.Run(s);
        Assert.Equal(a.NodeCount, b.NodeCount);
        for (uint i = 0; i < (uint)a.NodeCount; i++)
        {
            var va = a.GetNode(i);
            var vb = b.GetNode(i);
            Assert.Equal(va.Tier, vb.Tier);
            Assert.Equal(va.Atom, vb.Atom);
            Assert.Equal(va.FirstChildIdx, vb.FirstChildIdx);
            Assert.Equal(va.ChildCount, vb.ChildCount);
            Assert.Equal(va.ParentIdx, vb.ParentIdx);
        }
    }

    [Fact]
    public void NfcEquivalentInputs_ProduceIdenticalTrees()
    {
        // Precomposed é vs e + combining acute
        using var pre = TextDecomposer.Run(new byte[] { 0xC3, 0xA9 });
        using var dec = TextDecomposer.Run(new byte[] { 0x65, 0xCC, 0x81 });
        Assert.Equal(pre.NodeCount, dec.NodeCount);
        for (uint i = 0; i < (uint)pre.NodeCount; i++)
        {
            Assert.Equal(pre.GetNode(i).Atom, dec.GetNode(i).Atom);
        }
    }

    [Fact]
    public void HashComposerCanPopulateAfterDecompose()
    {
        // Full round-trip: decompose → HashComposer fills in the IDs.
        using var t = TextDecomposer.Run("hi");
        // Synthetic atom resolver for the test (since perfcache isn't
        // loaded in this test environment).
        unsafe
        {
            HashComposer.Run(t, &SynthResolver);
        }
        // After Run, the root id should be non-zero.
        var root = t.GetNode((uint)t.NodeCount - 1);
        Assert.NotEqual(Hash128.Zero, root.Id);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(
        CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe int SynthResolver(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        Span<byte> b = stackalloc byte[4];
        b[0] = (byte)(atom & 0xFF);
        b[1] = (byte)((atom >> 8) & 0xFF);
        b[2] = (byte)((atom >> 16) & 0xFF);
        b[3] = (byte)((atom >> 24) & 0xFF);
        *outId = Hash128.Blake3(b);
        outCoord[0] = (double)atom / 1000000.0;
        outCoord[1] = 0; outCoord[2] = 0; outCoord[3] = 0;
        Span<double> c = stackalloc double[4] { outCoord[0], 0, 0, 0 };
        *outHb = Hilbert128.Encode(c);
        return 0;
    }
}
