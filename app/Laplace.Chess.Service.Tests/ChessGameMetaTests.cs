using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// The conventional GAME-metadata tier: time-control classification (the bullet/blitz/rapid/classical
/// evidence axis) and the content-addressed game id (the same real game → one node, witnessed each time).
/// Pure logic, no DB.
/// </summary>
public sealed class ChessGameMetaTests
{
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
        Assert.Equal(a, b);   // same game across DBs → one content node, witnessed twice
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
