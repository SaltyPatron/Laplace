using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessGameMetaTests
{
    [Fact]
    public void InitialState_NoSetUpTag_UsesStandardStart()
    {
        var m = new ChessModality();
        var (initial, standard) = ChessPgnDecomposer.InitialState("[Event \"x\"]\n", m);
        Assert.True(standard);
        Assert.Equal(m.Initial().Board.ToFen(), initial.Board.ToFen());
    }

    [Fact]
    public void InitialState_SetUpWithValidFen_UsesThatPosition()
    {
        const string fen = "r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3";
        var gameText = $"[Event \"x\"]\n[SetUp \"1\"]\n[FEN \"{fen}\"]\n";
        var m = new ChessModality();
        var (initial, standard) = ChessPgnDecomposer.InitialState(gameText, m);
        Assert.False(standard);
        Assert.Equal(fen, initial.Board.ToFen());
    }

    [Fact]
    public void InitialState_SetUpWithGarbageFen_FallsBackToStandardWithoutThrowing()
    {
        var gameText = "[Event \"x\"]\n[SetUp \"1\"]\n[FEN \"not a real fen\"]\n";
        var m = new ChessModality();
        var (initial, standard) = ChessPgnDecomposer.InitialState(gameText, m);
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
