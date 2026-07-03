using System.IO;
using System.Text;
using Laplace.Engine.Core;
using Laplace.Modality;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class PgnGamesTests
{
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
        const string game = "[White \"Alice\"]\n[WhiteElo \"1500\"]\n";
        Assert.Equal("Alice", PgnGames.TagStr(game, "White"));
        Assert.Equal("1500", PgnGames.TagStr(game, "WhiteElo"));
    }

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

    private static PgnMovetext.PgnWalkResult WalkMovetext(string movetext)
    {
        var bytes = Encoding.UTF8.GetBytes(movetext + " 1-0");
        using var ast = GrammarDecomposer.Parse(bytes, "pgn");
        return PgnMovetext.Walk(ast, bytes);
    }

    private static List<string> ExtractMainline(string movetext)
    {
        var bytes = Encoding.UTF8.GetBytes(movetext + " 1-0");
        using var ast = GrammarDecomposer.Parse(bytes, "pgn");
        return PgnMovetext.Extract(ast, bytes).Moves;
    }

    [Fact]
    public void PgnMovetext_Extract_MainlineSansOnly()
    {
        var moves = ExtractMainline("1. e4 e5 2. Nf3 Nc6");
        Assert.Equal(["e4", "e5", "Nf3", "Nc6"], moves);
    }

    [Fact]
    public void PgnMovetext_Walk_SkipsVariationMoves()
    {
        var walk = WalkMovetext("1. e4 e5 (1... c5) 2. Nf3");
        var mainline = walk.Mainline.Select(s => s.San).ToList();
        Assert.Equal(["e4", "e5", "Nf3"], mainline);
        Assert.Contains(walk.AllPlies, s => s.InVariation && s.San == "c5");
    }

    [Fact]
    public void PgnMovetext_Walk_MainlinePlyIndexIsZeroBased()
    {
        var walk = WalkMovetext("1. e4 e5 2. Nf3");
        Assert.Equal([0, 1, 2], walk.Mainline.Select(s => s.PlyIndex));
    }

    [Fact]
    public void PgnMovetext_Walk_PostMoveCommentAligns()
    {
        var walk = WalkMovetext("1. e4 { [%clk 0:15:00] } e5");
        var e4 = walk.Mainline.First(s => s.San == "e4");
        Assert.Equal("[%clk 0:15:00]", e4.CommentText);
        Assert.Null(walk.Mainline.First(s => s.San == "e5").CommentText);
    }

    [Fact]
    public void PgnMovetext_Walk_PreMoveCommentAligns()
    {
        var walk = WalkMovetext("1. { opening } e4 e5");
        var e4 = walk.Mainline.First(s => s.San == "e4");
        Assert.Equal("opening", e4.CommentText);
    }

    [Fact]
    public void PgnMovetext_Walk_NagAlignsToPrecedingMove()
    {
        var walk = WalkMovetext("1. e4 e5 $2 2. Nf3");
        Assert.Equal(2, walk.Mainline.First(s => s.San == "e5").Nag);
    }

    [Fact]
    public void PgnMovetext_Walk_StandaloneAnnotationAligns()
    {
        var walk = WalkMovetext("1. e4 e5 2. Nc6 !?");
        var nc6 = walk.AllPlies.First(s => s.San == "Nc6");
        Assert.Equal("!?", nc6.StandaloneAnnotation);
    }

    [Fact]
    public void PgnEvals_ParseToken_DecimalPawns()
        => Assert.Equal(35, PgnEvals.ParseToken("0.35"));

    [Fact]
    public void PgnEvals_Centipawns_AlignedWithMoves()
    {
        const string game = """
            [Event "Test"]
            1. e4 {[%eval 0.35]} e5 {[%eval -0.12]} 1-0
            """;
        var cp = PgnEvals.Centipawns(game, 2);
        Assert.NotNull(cp);
        Assert.Equal(35, cp![0]);
        Assert.Equal(-12, cp[1]);
    }
}
