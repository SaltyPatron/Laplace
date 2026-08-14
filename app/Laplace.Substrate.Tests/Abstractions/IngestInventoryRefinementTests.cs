using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The progress denominator self-corrects: a sampled estimate stands until a
/// background exact count publishes, after which every reader sees the exact
/// total. Guards the contract that made input_pct read 111% on the 2026-08-13
/// wiktionary run impossible to reintroduce silently.
/// </summary>
public sealed class IngestInventoryRefinementTests
{
    [Fact]
    public void EffectiveTotal_IsTheEstimate_UntilRefined()
    {
        var inv = IngestInventory.Single(9_397_812);
        Assert.Equal(9_397_812, inv.EffectiveTotalInputUnits);
    }

    [Fact]
    public void EffectiveTotal_IsTheExactCount_OncePublished()
    {
        var inv = IngestInventory.Single(9_397_812);
        inv.PublishExactTotal(10_482_360);
        Assert.Equal(10_482_360, inv.EffectiveTotalInputUnits);
        Assert.Equal(9_397_812, inv.TotalInputUnits); // declared estimate is preserved
    }

    [Fact]
    public void PublishingZeroOrNegative_NeverClobbersTheEstimate()
    {
        var inv = IngestInventory.Single(500);
        inv.PublishExactTotal(0);
        Assert.Equal(500, inv.EffectiveTotalInputUnits);
        inv.PublishExactTotal(-1);
        Assert.Equal(500, inv.EffectiveTotalInputUnits);
    }

    [Fact]
    public void SmallFiles_GetExactCountsUpFront_NoRefinementNeeded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-inv-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, Enumerable.Repeat("{\"w\":1}", 1234));
        try
        {
            var inv = IngestInventory.FromFiles("jsonl", [path], maxInputUnits: 0);
            Assert.NotNull(inv);
            Assert.Equal(1234, inv!.TotalInputUnits);
            Assert.Equal(1234, inv.EffectiveTotalInputUnits);
        }
        finally { File.Delete(path); }
    }
}
