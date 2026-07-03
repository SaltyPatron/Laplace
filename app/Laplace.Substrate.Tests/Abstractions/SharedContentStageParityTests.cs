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
    [InlineData("A", 1)]
    [InlineData("̐", 1)]
    [InlineData("\u00e7", 1)]
    [InlineData("c\u0327", 1)]
    public void RootTier_Is_The_Natural_Unit(string s, int expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        Assert.True(TextEntityBuilder.TryBuildRows(bytes, Src, out _, out _, out _, out var rootTier));
        Assert.Equal(expected, (int)rootTier);
    }
}
