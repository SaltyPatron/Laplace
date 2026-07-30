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
        var (initial, standard) = ChessAnalyze.InitialState(null, m);
        Assert.True(standard);
        Assert.Equal(m.Initial().Board.ToFen(), initial.Board.ToFen());
    }

    [Fact]
    public void InitialState_ValidFen_UsesThatPosition()
    {
        const string fen = "r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3";
        var m = new ChessModality();
        var (initial, standard) = ChessAnalyze.InitialState(fen, m);
        Assert.False(standard);
        Assert.Equal(fen, initial.Board.ToFen());
    }

    [Fact]
    public void InitialState_GarbageFen_FallsBackToStandardWithoutThrowing()
    {
        var m = new ChessModality();
        var (initial, standard) = ChessAnalyze.InitialState("not a real fen", m);
        Assert.True(standard);
        Assert.Equal(m.Initial().Board.ToFen(), initial.Board.ToFen());
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
