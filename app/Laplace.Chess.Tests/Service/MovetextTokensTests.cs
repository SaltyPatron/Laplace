using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// The movetext's own units. Prose segmentation of PGN produced 82.5% single-use fragments
/// (81,373 constituents / 67,108 distinct over 3,000 games) because UAX #29 splits on '.',
/// which in PGN is a move-number separator. These pin the units that actually recur.
/// </summary>
public sealed class MovetextTokensTests
{
    [Fact]
    public void PliesAreTheirOwnTokens()
    {
        var t = MovetextTokens.Parse("1. e4 e6 2. d4 d5 1-0");
        Assert.Equal(["1.", "e4", "e6", "2.", "d4", "d5", "1-0"], t);
    }

    [Fact]
    public void ClockCommentsStayWhole()
    {
        // A naive whitespace split shreds this into "{[%clk" and "0:02:59.8]}" — two tokens
        // that mean nothing on their own.
        var t = MovetextTokens.Parse("1. d3 {[%clk 0:02:59.8]} d5");
        Assert.Equal(["1.", "d3", "{[%clk 0:02:59.8]}", "d5"], t);
    }

    [Fact]
    public void VariationsStayWhole()
    {
        var t = MovetextTokens.Parse("1. e4 (1. d4 d5) e5");
        Assert.Equal(["1.", "e4", "(1. d4 d5)", "e5"], t);
    }

    [Fact]
    public void TokensRebuildTheMovetext()
    {
        const string mt = "1. e4 e6 2. d4 d5 3. Nd2 Nf6 1-0";
        Assert.Equal(mt, MovetextTokens.Canonical(MovetextTokens.Parse(mt)));
    }

    [Fact]
    public void WhitespaceShapeDoesNotChangeTheUnits()
    {
        // A line break mid-game and a space mean the same game; the units must not differ.
        Assert.Equal(MovetextTokens.Parse("1. e4 e6\n2. d4 d5"),
                     MovetextTokens.Parse("1. e4 e6 2. d4 d5"));
    }

    [Fact]
    public void SharedPliesAreTheSameTokens_WhichIsThePoint()
    {
        // Two different games that both open 1.e4 e5 must reuse the SAME units. Under prose
        // segmentation they minted different fragments and never collided.
        var a = MovetextTokens.Parse("1. e4 e5 2. Nf3 Nc6 1-0");
        var b = MovetextTokens.Parse("1. e4 e5 2. Bc4 Nf6 0-1");
        Assert.Equal(a.Take(4), b.Take(4));
    }
}
