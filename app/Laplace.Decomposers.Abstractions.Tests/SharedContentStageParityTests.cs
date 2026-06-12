using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

// The coalescing law: a content witness's root, tier, and staged rows are a pure
// function of (canonical bytes, perfcache) — NEVER of which IntentStage receives
// them or what was staged before. The Stage-1 coalescing fix routed all witnesses
// through one shared stage; the laplace_minilm floor reseed then diverged from the
// pre-fix floor on 244 Unicode confusable targets (tier-1 roots became unary
// tier-2 wrappers that fail render). These pins decide stage-contamination vs
// native/C#-lane disagreement and hold the lane to the natural-unit law.
[Collection("GrammarPerfcache")]
public sealed class SharedContentStageParityTests
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/StageParity/v1");

    public static readonly TheoryData<string> Cases = new()
    {
        "dog",
        "̐",               // single combining mark (candrabindu class — live divergence)
        "ç",               // ç precomposed single grapheme
        "ç",              // c + combining cedilla: 2 codepoints, 1 grapheme
        "श्र",   // devanagari conjunct: multi-codepoint cluster
        "ab",                   // 2 graphemes, 1 word
        "A",                    // single ASCII letter word
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

    // The grapheme-floor law: single-grapheme content roots at the grapheme
    // (tier 1) no matter how many codepoints compose it — a unary word wrapper
    // is artificial inflation (and fails render). Multi-grapheme words root at
    // tier 2 as ever. Live regression source: 127 unrenderable unary tier-2
    // Unicode confusable targets, laplace_minilm 2026-06-12.
    [Theory]
    [InlineData("dog", 2)]
    [InlineData("ab", 2)]
    [InlineData("A", 1)]
    [InlineData("̐", 1)]                // U+0310, lone combining mark
    [InlineData("ç", 1)]                // precomposed
    [InlineData("ç", 1)]               // c + U+0327: 2 codepoints, 1 grapheme
    public void RootTier_Is_The_Natural_Unit(string s, int expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out _, out _, out _, out var rootTier));
        Assert.Equal(expected, (int)rootTier);
    }
}
