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

    [Fact]
    public void GameId_SameGame_SameNode()
    {
        var a = ChessVocabulary.GameId("Carlsen", "Nakamura", "2024.01.01", new[] { "e4", "e5", "Nf3" });
        var b = ChessVocabulary.GameId("Carlsen", "Nakamura", "2024.01.01", new[] { "e4", "e5", "Nf3" });
        Assert.Equal(a, b);
    }

    [Fact]
    public void GameId_DifferentMoves_DifferentNode()
    {
        var a = ChessVocabulary.GameId("Carlsen", "Nakamura", "2024.01.01", new[] { "e4", "e5" });
        var b = ChessVocabulary.GameId("Carlsen", "Nakamura", "2024.01.01", new[] { "d4", "d5" });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GameId_DifferentPlayers_DifferentNode()
    {
        var a = ChessVocabulary.GameId("Carlsen", "Nakamura", "2024.01.01", new[] { "e4", "e5" });
        var b = ChessVocabulary.GameId("Caruana", "Nakamura", "2024.01.01", new[] { "e4", "e5" });
        Assert.NotEqual(a, b);
    }
}
