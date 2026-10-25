using Xunit;
using Laplace.Engine.Core;

namespace Laplace.Engine.Core.Tests;

[Collection("Perfcache")]
public class TextDecomposerTests
{
    public TextDecomposerTests(PerfcacheTestFixture _) { }


    [Fact]
    public void NativeWordSegmentsPreserveOffsetsAndDoNotInferRoleFromTier()
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes("a mississippi e\u0301.");
        var segments = TextDecomposer.WordSegments(utf8);
        Assert.Equal(new[] { "a", "mississippi", "e\u0301", "." }, segments.Select(
            s => System.Text.Encoding.UTF8.GetString(utf8, s.Offset, s.Length)).ToArray());
        Assert.Equal(new[] { 0, 2, 14, 17 }, segments.Select(s => s.Offset).ToArray());
        foreach (var segment in segments)
            Assert.Equal(TextDecomposer.ContentRootId(utf8.AsSpan(segment.Offset, segment.Length)), segment.Id);
        using var word = TextDecomposer.Run("mississippi");
        Assert.Equal(1, word.GetNode(word.NaturalUnitIndex()).Tier);
        Assert.Equal(word.GetNode(word.NaturalUnitIndex()).Id, segments[1].Id);
    }

    [Fact]
    public void EmptyString_DoesNotMintAnEntity()
    {
        using var t = TextDecomposer.Run("");
        Assert.Equal(0, t.NodeCount);
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
        int leaves = 0;
        for (uint i = 0; i < (uint)t.NodeCount; i++)
        {
            var v = t.GetNode(i);
            if (v.Tier == 0) leaves++;
        }
        Assert.Equal(12, leaves);
        var root = t.GetNode(t.NaturalUnitIndex());
        Assert.True(root.ChildCount >= 2);
        Assert.Equal(Hash128.Text("Hello world."), root.Id);
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
    public void UnicodeRepresentationsRemainDistinctUntilExplicitNormalization()
    {





        using var pre = TextDecomposer.Run(new byte[] { 0xC3, 0xA9 });
        using var dec = TextDecomposer.Run(new byte[] { 0x65, 0xCC, 0x81 });
        Assert.Equal(0x00E9u, pre.GetNode(0).Atom);
        Assert.Equal(0x0065u, dec.GetNode(0).Atom);
        Assert.Equal(0x0301u, dec.GetNode(1).Atom);
        Assert.NotEqual(pre.GetNode(pre.NaturalUnitIndex()).Id, dec.GetNode(dec.NaturalUnitIndex()).Id);
    }

    [Fact]
    public void SourceRoot_PreservesAuthoredUnicodeRepresentation()
    {
        CodepointPerfcache.LoadDefault();
        byte[] precomposed = [0xC3, 0xA9];
        byte[] decomposed = [0x65, 0xCC, 0x81];

        Assert.NotEqual(
            TextDecomposer.ContentRootId(precomposed),
            TextDecomposer.ContentRootId(decomposed));
        Assert.NotEqual(
            TextDecomposer.SourceRootId(precomposed),
            TextDecomposer.SourceRootId(decomposed));
    }

    [Fact]
    public void SourceTreeRoot_EqualsDirectSourceRoot()
    {
        CodepointPerfcache.LoadDefault();
        byte[] source = "def multiply(a, b):\n    return a * b\n"u8.ToArray();
        using var tree = IntentStage.BuildSourceContentTree(source);
        Assert.NotNull(tree);

        Hash128 direct = Assert.IsType<Hash128>(TextDecomposer.SourceRootId(source));
        Assert.Equal(direct, tree.GetNode(tree.NaturalUnitIndex()).Id);
    }

    [Fact]
    public void HashComposerCanPopulateAfterDecompose()
    {
        using var t = TextDecomposer.Run("hi");
        unsafe
        {
            HashComposer.Run(t, &SynthResolver);
        }
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
