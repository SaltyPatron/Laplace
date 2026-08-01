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

    [Theory]
    [InlineData("0:03:00", 180d)]
    [InlineData("1:02:03.5", 3723.5d)]
    public void TryParseHms_ParsesValid(string token, double expected)
    {
        Assert.True(PgnClocks.TryParseHms(token, out double sec));
        Assert.Equal(expected, sec);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3:00")]
    [InlineData("0:xx:00")]
    [InlineData("0:03:nope")]
    public void TryParseHms_RejectsMalformedWithoutThrowing(string? token)
        => Assert.False(PgnClocks.TryParseHms(token, out _));

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

    [Fact]
    public void MedianRemaining_IsPerSide()
    {
        var clocks = PgnClocks.SecondsRemaining(Movetext, 6);
        // parity 0 (first mover): 180/175/155 -> 175; parity 1: 180/178/177 -> 178.
        Assert.Equal(175d, PgnClocks.MedianRemaining(clocks, 0));
        Assert.Equal(178d, PgnClocks.MedianRemaining(clocks, 1));
    }

    [Fact]
    public void MedianRemaining_ZeroWhenNoClockStory()
    {
        Assert.Equal(0d, PgnClocks.MedianRemaining(System.Array.Empty<double>(), 0));
        Assert.Equal(0d, PgnClocks.MedianRemaining(new[] { 0d, 0d, 0d, 0d }, 1));
    }

    // ---- the think lenses over synthetic whole-game clock sequences, both dialects ----

    [Fact]
    public void ThinkLens_LichessDialect_FlagsTheBurnedDownClock()
    {
        // Synthetic 12-ply story: the first mover burns 60s down to 2s while the
        // opponent cruises. medianDrop = 5s, so the 2s clock at ply 10 cannot fund one
        // median think — flagging, even though the move itself was a long (deep) one.
        var clocks = new[] { 60d, 60d, 55, 58, 45, 56, 30, 54, 12, 52, 2, 50 };
        double medianDrop = PgnClocks.MedianDrop(clocks);
        double medEven = PgnClocks.MedianRemaining(clocks, 0);
        double tf = PgnClocks.ThinkFactor(clocks, medianDrop, 10);
        Assert.Equal("flagging",
            ChessCanonical.ThinkLens(10, clocks.Length, tf, clocks[10], medEven, medianDrop));
        // The cruising opponent at the same point of the game shows no lens.
        double tfOpp = PgnClocks.ThinkFactor(clocks, medianDrop, 11);
        Assert.Null(ChessCanonical.ThinkLens(
            11, clocks.Length, tfOpp, clocks[11], PgnClocks.MedianRemaining(clocks, 1), medianDrop));
    }

    [Fact]
    public void ThinkLens_SpentDialect_EarlyBookIsPlannedQuick_LateSnapIsNot()
    {
        // cutechess dialect: spent time only, no remaining clock ever witnessed.
        var spent = new[] { 0.1, 0.1, 0.2, 0.1, 5.0, 6.0, 5.5, 4.0, 5.0, 6.0, 5.0, 0.5 };
        double med = PgnClocks.MedianSpent(spent);
        double tf0 = PgnClocks.ThinkFactorFromSpent(spent, med, 0);
        Assert.Equal("planned_quick",
            ChessCanonical.ThinkLens(0, spent.Length, tf0, 0, 0, 0));
        // Mid-game normal think: no lens.
        double tf6 = PgnClocks.ThinkFactorFromSpent(spent, med, 6);
        Assert.Null(ChessCanonical.ThinkLens(6, spent.Length, tf6, 0, 0, 0));
        // A late snap move is base "rushed" only — and with no witnessed clock the
        // spent dialect can never fabricate flagging or pressed_think.
        double tf11 = PgnClocks.ThinkFactorFromSpent(spent, med, 11);
        Assert.Null(ChessCanonical.ThinkLens(11, spent.Length, tf11, 0, 0, 0));
    }
}
