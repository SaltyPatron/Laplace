using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class PgnClocksTests
{
    private const string Movetext =
        "1. e4 {[%clk 0:03:00]} 1... e5 {[%clk 0:03:00]} " +
        "2. Nf3 {[%clk 0:02:55]} 2... Nc6 {[%clk 0:02:58]} " +
        "3. Bb5 {[%clk 0:02:35]} 3... a6 {[%clk 0:02:57]} 1-0";

    [Fact]
    public void SecondsRemaining_ParsesAndAligns()
    {
        var s = PgnClocks.SecondsRemaining(Movetext, 6);
        Assert.Equal(new[] { 180d, 180d, 175d, 178d, 155d, 177d }, s);
    }

    [Fact]
    public void SecondsRemaining_EmptyWhenCountMismatch()
        => Assert.Empty(PgnClocks.SecondsRemaining(Movetext, 5));

    [Fact]
    public void SecondsRemaining_EmptyWhenNoClocks()
        => Assert.Empty(PgnClocks.SecondsRemaining("1. e4 e5 2. Nf3 Nc6 1-0", 4));

    [Fact]
    public void ThinkFactor_RushedMoveIsDownWeighted_DeliberateIsUp()
    {
        var clocks = PgnClocks.SecondsRemaining(Movetext, 6);
        double median = PgnClocks.MedianDrop(clocks);
        Assert.True(PgnClocks.ThinkFactor(clocks, median, 4) > 1.0);
        Assert.True(PgnClocks.ThinkFactor(clocks, median, 3) < 1.0);
        Assert.Equal(1.0, PgnClocks.ThinkFactor(clocks, median, 0));
    }

    [Fact]
    public void ThinkFactor_NeutralWhenNoClocks()
        => Assert.Equal(1.0, PgnClocks.ThinkFactor(System.Array.Empty<double>(), 0, 5));

    // cutechess-cli dialect: per-move time SPENT ("0.13s" in "{+0.48/17 0.13s}"), GH #494.
    private const string CutechessMovetext =
        "1. e4 {+0.28/12 0.95s} 1... e5 {-0.21/14 1.02s} " +
        "2. Nf3 {+0.35/13 0.98s} 2... Nc6 {-0.30/15 3.50s} " +
        "3. Bb5 {+M3/10 0.05s} 3... a6 {-0.41/12 1.00s} 1-0";

    [Fact]
    public void SpentSeconds_ParsesCutechessComments()
    {
        var s = PgnClocks.SpentSeconds(CutechessMovetext, 6);
        Assert.NotNull(s);
        Assert.Equal(new[] { 0.95, 1.02, 0.98, 3.50, 0.05, 1.00 }, s);
    }

    [Fact]
    public void SpentSeconds_NullOnMismatchOrLichessFormat()
    {
        Assert.Null(PgnClocks.SpentSeconds(CutechessMovetext, 5));
        Assert.Null(PgnClocks.SpentSeconds(Movetext, 6));
        Assert.Null(PgnClocks.SpentSeconds("1. e4 e5 1-0", 2));
    }

    [Fact]
    public void ThinkFactorFromSpent_LongThinkUp_SnapMoveDown()
    {
        var spent = PgnClocks.SpentSeconds(CutechessMovetext, 6)!;
        double median = PgnClocks.MedianSpent(spent);
        Assert.True(median > 0);
        Assert.True(PgnClocks.ThinkFactorFromSpent(spent, median, 3) > 1.0);  // 3.50s think
        Assert.True(PgnClocks.ThinkFactorFromSpent(spent, median, 4) < 1.0);  // 0.05s snap
        Assert.Equal(1.0, PgnClocks.ThinkFactorFromSpent(spent, 0, 1));       // no median → neutral
    }
}
