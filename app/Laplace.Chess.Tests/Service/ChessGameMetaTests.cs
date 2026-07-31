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

    // The case this actually costs: chess.com exports X-FEN/Shredder file-letter castling for
    // every Chess960 game (GBgb, GDgd, GCgc ...). Board.FromFen used to map each unknown
    // letter to CastleRights.None, so the game replayed as one whose rooks could not castle.
    [Theory]
    [InlineData("qrbbnkrn/pppppppp/8/8/8/8/PPPPPPPP/QRBBNKRN w GBgb - 0 1")]
    [InlineData("nbbrknrq/pppppppp/8/8/8/8/PPPPPPPP/NBBRKNRQ w GDgd - 0 1")]
    [InlineData("nqrkbbrn/pppppppp/8/8/8/8/PPPPPPPP/NQRKBBRN w GCgc - 0 1")]
    public void InitialState_Chess960XFenCastling_IsRefusedNotSilentlyStripped(string fen)
    {
        var m = new ChessModality();
        Assert.Null(ChessAnalyze.InitialState(fen, m));
    }

    [Fact]
    public void FromFen_MalformedCounter_NamesTheField()
    {
        var ex = Assert.Throws<FormatException>(
            () => Board.FromFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - x 1"));
        Assert.Contains("halfmove clock", ex.Message);
    }

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
