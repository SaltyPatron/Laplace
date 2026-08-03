using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessGameMetaTests
{
    // InitialState is now the analyzer's (calculated) FEN->board step; the SetUp/FEN extraction
    // from PGN text is the recorder's job (ChessPgnDecomposer.RecordStartPosition, covered in
    // ChessRecorderTests). See docs/specs/08_Record_vs_Calculate_Spec.txt.
    [Fact]
    public void InitialState_NoFen_UsesStandardStart()
    {
        var m = new ChessModality();
        var (initial, standard) = Assert.NotNull(ChessAnalyze.InitialState(null, m));
        Assert.True(standard);
        Assert.Equal(m.Initial().Board.ToFen(), initial.Board.ToFen());
    }

    [Fact]
    public void InitialState_ValidFen_UsesThatPosition()
    {
        const string fen = "r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3";
        var m = new ChessModality();
        var (initial, standard) = Assert.NotNull(ChessAnalyze.InitialState(fen, m));
        Assert.False(standard);
        Assert.Equal(fen, initial.Board.ToFen());
    }

    // Was InitialState_GarbageFen_FallsBackToStandardWithoutThrowing, which pinned the
    // substitution as intended behaviour. It is not: a start position we cannot read is not
    // the standard start, and replaying a game from a board it never had records a game that
    // was never played -- under a run reporting status=ok, failed=0.
    [Fact]
    public void InitialState_GarbageFen_IsRefused()
    {
        var m = new ChessModality();
        Assert.Null(ChessAnalyze.InitialState("not a real fen", m));
    }

    // chess.com exports X-FEN/Shredder file-letter castling for every Chess960 game (GBgb,
    // GDgd, GCgc ...). Three behaviours have now existed here, and only the third is right:
    //   1. map each unknown letter to CastleRights.None -> the game replayed as one whose
    //      rooks could not castle. A wrong game, recorded and folded.
    //   2. refuse the position outright -> honest, and cost 1,866 games.
    //   3. model it. The rights are read, the rooks keep their files, the game replays.
    [Theory]
    [InlineData("qrbbnkrn/pppppppp/8/8/8/8/PPPPPPPP/QRBBNKRN w GBgb - 0 1")]
    [InlineData("nbbrknrq/pppppppp/8/8/8/8/PPPPPPPP/NBBRKNRQ w GDgd - 0 1")]
    [InlineData("nqrkbbrn/pppppppp/8/8/8/8/PPPPPPPP/NQRKBBRN w GCgc - 0 1")]
    public void InitialState_Chess960XFenCastling_IsModelledNotStripped(string fen)
    {
        var m = new ChessModality();
        var start = ChessAnalyze.InitialState(fen, m);

        Assert.NotNull(start);
        // NOT the standard array — the whole failure mode this guards is replaying a
        // Chess960 game from rnbqkbnr/... and calling it the same game.
        Assert.False(start!.Value.StandardStart);
        Assert.Equal(CastleRights.All, start.Value.Initial.Board.Castle);
        Assert.False(start.Value.Initial.Board.StandardCastleFiles);
    }

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

    [Theory]
    [InlineData("60", "bullet")]
    [InlineData("120+1", "bullet")]
    [InlineData("180", "blitz")]
    [InlineData("300+2", "blitz")]
    [InlineData("600", "rapid")]
    [InlineData("900+10", "rapid")]
    [InlineData("1800", "classical")]
    [InlineData("40/7200:1800", "classical")]
    [InlineData("-", "")]
    [InlineData("", "")]
    [InlineData("garbage", "")]
    public void TcClass_ClassifiesByBaseSeconds(string tc, string expected)
        => Assert.Equal(expected, ChessPgnDecomposer.TcClass(tc));

    // GH #736: the old GameId(white, black, date, moves) identity tests lived here; the
    // line/event identity law they were replaced by is pinned in ChessLineIdentityTests.
}
