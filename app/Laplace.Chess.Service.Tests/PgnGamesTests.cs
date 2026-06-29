using System.IO;
using System.Text;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Unit tests for the shared PGN parsing helpers (<see cref="PgnGames"/>): tag scanning and the
/// lazy game splitter. Pure string logic, no DB, no native DLL — runs in any environment.
/// </summary>
[Trait("Tier", "fast")]
public sealed class PgnGamesTests
{
    // ----- TagStr -----

    [Fact]
    public void TagStr_ReturnsValue_WhenPresent()
    {
        const string game = "[Event \"World Championship\"]\n[White \"Carlsen, Magnus\"]\n";
        Assert.Equal("World Championship", PgnGames.TagStr(game, "Event"));
        Assert.Equal("Carlsen, Magnus", PgnGames.TagStr(game, "White"));
    }

    [Fact]
    public void TagStr_ReturnsEmpty_WhenTagAbsent()
        => Assert.Equal("", PgnGames.TagStr("[White \"Alice\"]\n", "Black"));

    [Fact]
    public void TagStr_TrimsWhitespace()
    {
        const string game = "[Site \"  Berlin  \"]\n";
        Assert.Equal("Berlin", PgnGames.TagStr(game, "Site"));
    }

    [Fact]
    public void TagStr_HandlesConsecutiveTags_NoFalseMatch()
    {
        // "WhiteElo" tag must not match a search for "White".
        const string game = "[White \"Alice\"]\n[WhiteElo \"1500\"]\n";
        Assert.Equal("Alice", PgnGames.TagStr(game, "White"));
        Assert.Equal("1500", PgnGames.TagStr(game, "WhiteElo"));
    }

    // ----- TagInt -----

    [Fact]
    public void TagInt_ReturnsValue_WhenPresent()
    {
        const string game = "[WhiteElo \"2830\"]\n[BlackElo \"1600\"]\n";
        Assert.Equal(2830, PgnGames.TagInt(game, "WhiteElo"));
        Assert.Equal(1600, PgnGames.TagInt(game, "BlackElo"));
    }

    [Fact]
    public void TagInt_ReturnsZero_WhenAbsent()
        => Assert.Equal(0, PgnGames.TagInt("[White \"Alice\"]\n", "WhiteElo"));

    [Fact]
    public void TagInt_ReturnsZero_WhenNotNumeric()
        => Assert.Equal(0, PgnGames.TagInt("[WhiteElo \"?\"]\n", "WhiteElo"));

    // ----- StreamGames -----

    [Fact]
    public void StreamGames_SplitsMultipleGames()
    {
        const string pgn =
            "[Event \"A\"]\n[White \"Alice\"]\n\n1. e4 e5 1-0\n\n" +
            "[Event \"B\"]\n[White \"Bob\"]\n\n1. d4 d5 0-1\n";
        var file = WriteTempPgn(pgn);
        var games = PgnGames.StreamGames(file).ToList();
        Assert.Equal(2, games.Count);
        Assert.Contains("Event \"A\"", games[0]);
        Assert.Contains("Event \"B\"", games[1]);
    }

    [Fact]
    public void StreamGames_SingleGame_ReturnsOneEntry()
    {
        const string pgn = "[Event \"Solo\"]\n[White \"Alice\"]\n\n1. e4 1-0\n";
        var file = WriteTempPgn(pgn);
        var games = PgnGames.StreamGames(file).ToList();
        Assert.Single(games);
        Assert.Contains("Event \"Solo\"", games[0]);
    }

    [Fact]
    public void StreamGames_EmptyFile_ReturnsNone()
    {
        var file = WriteTempPgn("");
        Assert.Empty(PgnGames.StreamGames(file));
    }

    [Fact]
    public void StreamGames_PreservesTagsAndMovetext()
    {
        const string pgn =
            "[Event \"Test\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Result \"0-1\"]\n\n" +
            "1. f3 e5 2. g4 Qh4# 0-1\n";
        var file = WriteTempPgn(pgn);
        var game = PgnGames.StreamGames(file).Single();
        Assert.Contains("White \"Alice\"", game);
        Assert.Contains("Qh4#", game);
    }

    private static string WriteTempPgn(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }
}
