using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessCanonicalTests
{
    [Theory]
    [InlineData(300, "0:05:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(0, "0:00:00")]
    public void ClockFromSeconds_Canonical(double sec, string expected)
        => Assert.Equal(expected, ChessCanonical.ClockFromSeconds(sec));

    [Fact]
    public void ClockFromMatch_NormalizesPadding()
    {
        Assert.Equal("0:05:00", ChessCanonical.ClockFromMatch("0", "5", "0"));
        Assert.Equal("1:01:01", ChessCanonical.ClockFromMatch("1", "1", "1"));
    }

    [Fact]
    public void ClockTokens_DedupeViaContentEmitter()
    {
        var idA = ContentEmitter.RootId("0:05:00");
        var idB = ContentEmitter.RootId("0:05:00");
        var idC = ContentEmitter.RootId("0:05:01");
        Assert.NotNull(idA);
        Assert.Equal(idA, idB);
        Assert.NotEqual(idA, idC);
    }

    [Theory]
    [InlineData(" 0.35 ", "0.35")]
    [InlineData("#-3", "#-3")]
    public void EvalToken_Trims(string raw, string expected)
        => Assert.Equal(expected, ChessCanonical.EvalToken(raw));

    [Theory]
    [InlineData(0.5, "rushed")]
    [InlineData(1.0, "normal")]
    [InlineData(1.5, "deep")]
    public void ThinkClass_Buckets(double tf, string expected)
        => Assert.Equal(expected, ChessCanonical.ThinkClass(tf));

    // Phase × clock × spent lens. Every threshold is the game's own: medianRemaining is
    // the player's median remaining clock, medianDrop the game's median per-move cost,
    // the phase bound a tertile of the game's length, and tf is relative to the game's
    // median think. remaining/medianRemaining/medianDrop = 0 is the cutechess spent
    // dialect (no remaining clock witnessed).
    [Theory]
    // flagging: remaining below the game's own median per-move cost, at any speed
    [InlineData(40, 60, 0.5, 5, 60, 10, "flagging")]
    [InlineData(40, 60, 1.5, 5, 60, 10, "flagging")]
    // pressed_think: a long think on a low clock — the critical-moment signal
    [InlineData(40, 60, 1.5, 30, 60, 10, "pressed_think")]
    // planned_quick: early phase, fast, no clock pressure — both dialects
    [InlineData(2, 60, 0.5, 170, 100, 10, "planned_quick")]
    [InlineData(2, 60, 0.5, 0, 0, 0, "planned_quick")]
    // early + fast but ALREADY low on clock is not book preparation
    [InlineData(2, 60, 0.5, 30, 60, 10, null)]
    // late + fast + low clock IS the base "rushed" cell — no second deposit
    [InlineData(50, 60, 0.5, 30, 60, 10, null)]
    // mid-game normal think: no lens adds information
    [InlineData(30, 60, 1.0, 100, 60, 10, null)]
    // the spent dialect carries no clock: deep never fabricates pressed_think
    [InlineData(40, 60, 1.5, 0, 0, 0, null)]
    public void ThinkLens_PhaseClockSpent(
        int ply, int plyCount, double tf, double remaining, double medianRemaining,
        double medianDrop, string? expected)
        => Assert.Equal(expected,
            ChessCanonical.ThinkLens(ply, plyCount, tf, remaining, medianRemaining, medianDrop));
}
