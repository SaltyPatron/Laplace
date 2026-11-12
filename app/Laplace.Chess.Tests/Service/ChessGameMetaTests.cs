using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessGameMetaTests
{
    [Fact]
    public void FromFen_MalformedCounter_NamesTheField()
    {
        var ex = Assert.Throws<FormatException>(
            () => Board.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - x 1"));
        Assert.Contains("halfmove clock", ex.Message);
    }

    [Fact]
    public void FromFen_MalformedFullmove_NamesTheField()
    {
        var ex = Assert.Throws<FormatException>(
            () => Board.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 nope"));
        Assert.Contains("fullmove number", ex.Message);
    }

    /// <summary>
    /// X-FEN castling is now READ, not refused — Chess960 is modelled. The concern the
    /// original refusal protected is unchanged and still asserted here: the rights must
    /// not be silently stripped, because a position that lost them mints a different id
    /// than the one the source asserted. It used to throw; now it must parse EXACTLY.
    /// </summary>
    [Fact]
    public void FromFen_XFenCastlingLetter_ParsesRightsAndRookFiles()
    {
        var b = Board.FromFen("qrbbnkrn/pppppppp/8/8/8/8/PPPPPPPP/QRBBNKRN w GBgb - 0 1");

        Assert.Equal(CastleRights.All, b.Castle);          // nothing stripped
        Assert.Equal(6, b.WhiteKingRookFile);              // G
        Assert.Equal(1, b.WhiteQueenRookFile);             // B
        Assert.Equal(6, b.BlackKingRookFile);
        Assert.Equal(1, b.BlackQueenRookFile);
        Assert.Equal("GBgb", b.CastleString());            // round-trips, not KQkq
        Assert.False(b.StandardCastleFiles);
    }

    /// <summary>A castling letter naming a file with no rook on it is still a hard error —
    /// the refuse-not-invent law survives, it just applies to a smaller set now.</summary>
    [Fact]
    public void FromFen_CastlingRightWithNoRook_StillThrows()
        => Assert.Throws<FormatException>(
            () => Board.FromFen("4k3/8/8/8/8/8/8/4K3 w K - 0 1"));

    // GH #736: the old GameId(white, black, date, moves) identity tests lived here; the
    // line/event identity law they were replaced by is pinned in ChessLineIdentityTests.
}
