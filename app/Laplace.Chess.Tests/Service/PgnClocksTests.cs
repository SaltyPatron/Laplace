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
}
