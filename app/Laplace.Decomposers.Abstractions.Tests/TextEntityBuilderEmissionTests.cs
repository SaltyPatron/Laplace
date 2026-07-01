using System.Collections.Generic;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class TextEntityBuilderEmissionTests
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/TextEmission/v1");

    [Theory]
    [InlineData("foo.\r\n\r\n")]
    [InlineData(" to hear about new eBooks.\r\n\r\n")]
    public void TrailingDoubleNewline_RoundtripsFromPhysicalities(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out _, out var phys, out var rootId, out _));
        byte[] rebuilt = ReconstructFromPhysicalities(phys, rootId);
        Assert.Equal(Nfc(bytes), rebuilt);
    }

    private static readonly string GalileoPath =
        Environment.GetEnvironmentVariable("LAPLACE_TEST_TEXT") is { Length: > 0 } dir
            ? Path.Combine(dir, "galileo.txt")
            : (OperatingSystem.IsWindows()
                ? @"D:\Data\Ingest\test-data\text\galileo.txt"
                : "/vault/Data/test-data/text/galileo.txt");

    [Fact]
    public void FullGalileo_InMemory_RoundtripsFromPhysicalities()
    {
        byte[] bytes = File.ReadAllBytes(GalileoPath);
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out _, out var phys, out var rootId, out _));
        byte[] rebuilt = ReconstructFromPhysicalities(phys, rootId);
        Assert.Equal(Nfc(bytes), rebuilt);
    }




    private static unsafe byte[] Nfc(byte[] utf8)
    {
        if (utf8.Length == 0) return utf8;
        byte* outPtr = null;
        nuint outLen = 0;
        fixed (byte* p = utf8)
        {
            if (NativeInterop.NormalizeNfcUtf8(p, (nuint)utf8.Length, &outPtr, &outLen) != 0)
                return utf8;
        }
        try { return new ReadOnlySpan<byte>(outPtr, (int)outLen).ToArray(); }
        finally { if (outPtr != null) System.Runtime.InteropServices.NativeMemory.Free(outPtr); }
    }

    private static byte[] ReconstructFromPhysicalities(
        System.Collections.Immutable.ImmutableArray<PhysicalityRow> phys, Hash128 rootId)
    {
        var idToCp = new Dictionary<Hash128, uint>(1_114_112);
        ReadOnlySpan<CodepointRecord> recs = CodepointPerfcache.Records;
        for (int i = 0; i < recs.Length; i++) idToCp[recs[i].Hash] = recs[i].Codepoint;

        var children = new Dictionary<Hash128, Hash128[]>();
        foreach (var p in phys)
        {
            if (p.TrajectoryXyzm is not { Length: > 0 } xyzm) continue;
            children[p.EntityId] = Trajectory.Constituents(xyzm);
        }

        var sb = new StringBuilder();
        Emit(rootId, children, idToCp, sb);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Emit(Hash128 id, Dictionary<Hash128, Hash128[]> children,
                             Dictionary<Hash128, uint> idToCp, StringBuilder sb)
    {
        if (idToCp.TryGetValue(id, out uint cp)) { sb.Append(char.ConvertFromUtf32((int)cp)); return; }
        if (children.TryGetValue(id, out var kids))
            foreach (var k in kids) Emit(k, children, idToCp, sb);
    }

    [Fact]
    public void SingleWord_Suppresses_Document_And_Sentence_Wrappers()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("dog");
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out var ents, out _, out var rootId, out var rootTier));

        Assert.Equal(EntityTier.Word, rootTier);
        Assert.DoesNotContain(ents, e => e.Tier >= EntityTier.Sentence);

        var root = Assert.Single(ents, e => e.Id.EqualsBytewise(rootId));
        Assert.Equal(EntityTier.Word, root.Tier);
        Assert.Equal(TextEntityBuilder.WordTypeId, root.TypeId);
    }

    [Fact]
    public void MultiSentence_Keeps_Structural_Tiers()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("Hello world. Second sentence.");
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out var ents, out _, out _, out _));
        Assert.Contains(ents, e => e.Tier == EntityTier.Sentence);
    }

    [Fact]
    public void DistributionalBigrams_Carry_Containing_Document_As_Context()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("Brave whales chase tiny boats. Second sentence holds more words.");
        Assert.True(TextEntityBuilder.TryBuildContentWitness(bytes, Src, 1.0,
            out var ents, out _, out var atts, out var rootId, out _));

        Assert.NotEmpty(atts);
        Assert.All(atts, a =>
        {
            Assert.True(a.ContextId.HasValue);
            Assert.True(a.ContextId!.Value.EqualsBytewise(rootId));
        });
        Assert.Contains(ents, e => e.Id.EqualsBytewise(rootId));
    }
}
