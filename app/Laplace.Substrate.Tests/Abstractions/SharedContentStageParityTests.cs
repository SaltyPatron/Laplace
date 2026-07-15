using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;








[Collection("GrammarPerfcache")]
public sealed class SharedContentStageParityTests
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/StageParity/v1");

    public static readonly TheoryData<string> Cases = new()
    {
        "dog",
        "̐",
        "ç",
        "ç",
        "श्र",
        "ab",
        "A",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void FreshStage_And_SharedStage_AgreeOnRoot(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);

        using var fresh = IntentStage.New(System.Math.Max(32, bytes.Length));
        Assert.True(fresh.TryAddContentWitness(bytes, Src, out var rootFresh));

        using var shared = IntentStage.New(256);
        Assert.True(shared.TryAddContentWitness(Encoding.UTF8.GetBytes("hello world"), Src, out _));
        Assert.True(shared.TryAddContentWitness(Encoding.UTF8.GetBytes("Second sentence, longer this time."), Src, out _));
        Assert.True(shared.TryAddContentWitness(bytes, Src, out var rootShared));

        Assert.True(rootFresh.EqualsBytewise(rootShared),
            $"root diverged for '{s}': fresh={rootFresh} shared={rootShared}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NativeLane_Agrees_With_CSharpLane(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);

        using var stage = IntentStage.New(System.Math.Max(32, bytes.Length));
        Assert.True(stage.TryAddContentWitness(bytes, Src, out var nativeRoot));

        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out var ents, out _, out var csRoot, out var csTier));

        Assert.True(nativeRoot.EqualsBytewise(csRoot),
            $"lanes diverged for '{s}': native={nativeRoot} cs={csRoot} (cs tier {csTier})");
    }






    [Theory]
    [InlineData("dog", 2)]
    [InlineData("ab", 2)]
    // Tier is a FLOOR: a single-codepoint grapheme IS the codepoint (same bytes,
    // same hash, one id) — the root of "A" is the tier-0 codepoint, not a
    // pass-through tier-1 wrapper. Only a genuinely multi-codepoint cluster
    // (c + combining cedilla) roots at the grapheme tier.
    [InlineData("A", 0)]
    [InlineData("̐", 0)]
    [InlineData("\u00e7", 0)]
    // NFC composes c+cedilla to U+00E7 at the text-lane boundary (text_decomposer.c)
    // -> single codepoint -> floor 0. q+combining-acute has NO precomposed form,
    // so it survives NFC as a genuine 2-codepoint cluster -> grapheme tier 1.
    [InlineData("c\u0327", 0)]
    [InlineData("q\u0301", 1)]
    public void RootTier_Is_The_Natural_Unit(string s, int expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out _, out _, out _, out var rootTier));
        Assert.Equal(expected, (int)rootTier);
    }
}
