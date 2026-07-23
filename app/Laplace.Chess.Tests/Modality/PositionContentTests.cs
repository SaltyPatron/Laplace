using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

public sealed class PositionContentTests
{
    [Fact]
    public void Rekey_AppendsFeatureTokens_WhenEnabled()
    {
        PositionContent.IncludeFeatureTokens = true;
        try
        {
            var b = Board.FromFen("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1");
            string s = PositionContent.Surface(b, "e3");
            Assert.Contains(" mob:", s);
        }
        finally
        {
            PositionContent.IncludeFeatureTokens = false;
        }
    }

    [Fact]
    public void Rekey_OmitsFeatureTokens_ByDefault()
    {
        PositionContent.IncludeFeatureTokens = false;
        var b = Board.FromFen("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1");
        string s = PositionContent.Surface(b, "e3");
        Assert.DoesNotContain(" mob:", s);
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", "-")]
    [InlineData("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1", "e3")]
    [InlineData("8/5k2/8/8/3K4/8/8/4R3 w - - 12 40", "-")]
    public void FenFromSurface_RoundTripsBoardFields(string fen, string canonicalEp)
    {
        var b = Board.FromFen(fen);
        string surface = PositionContent.Surface(b, canonicalEp);

        Assert.True(PositionContent.TryFenFromSurface(surface, out var rebuilt));
        // The surface carries no move counters, so compare the four board-defining fields.
        var expect = fen.Split(' ');
        var got = rebuilt.Split(' ');
        Assert.Equal(expect[0], got[0]); // placement
        Assert.Equal(expect[1], got[1]); // side to move
        Assert.Equal(expect[2], got[2]); // castling
        Assert.Equal(canonicalEp, got[3]); // ep from the canonical surface field
    }

    [Fact]
    public void FenFromSurface_RejectsNonSurfaceText()
    {
        Assert.False(PositionContent.TryFenFromSurface("not a surface at all", out _));
        Assert.False(PositionContent.TryFenFromSurface("", out _));
    }
}
