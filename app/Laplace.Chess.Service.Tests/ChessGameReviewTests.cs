using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessGameReviewTests
{
    private const string FoolsMate =
        "[Event \"Test\"]\n" +
        "[White \"Alice\"]\n" +
        "[Black \"Bob\"]\n" +
        "[WhiteElo \"1500\"]\n" +
        "[BlackElo \"1600\"]\n" +
        "[Result \"0-1\"]\n\n" +
        "1. f3 e5 2. g4 Qh4# 0-1\n";

    private const string BackrankMate =
        "[Event \"Test\"]\n" +
        "[White \"W\"]\n" +
        "[Black \"B\"]\n" +
        "[Result \"1-0\"]\n\n" +
        "1. e4 e5 2. Nf3 Nc6 3. Bc4 Nf6 4. Ng5 d5 5. exd5 Na5 6. Bb5+ c6 7. dxc6 bxc6 8. Be2 h6 " +
        "9. Nf3 e4 10. Ne5 Bc5 11. d4 exd3 12. Nxd3 Bb6 13. O-O O-O 14. Nc3 Re8 15. Bg5 " +
        "16. h3 Be6 17. Bf4 Nd5 18. Nxd5 cxd5 19. c3 Nb7 20. Bxb6 axb6 21. Qd2 Nc5 " +
        "22. Nxc5 bxc5 23. Bh5 Re5 24. Bg4 Bxg4 25. hxg4 Qd7 26. Rae1 Rxe1 27. Rxe1 Qxg4 " +
        "28. Re8+ Rxe8 29. Qd1 Qxd1# 0-1\n";

    private const string NoResult =
        "[Event \"Test\"]\n[White \"?\"]\n[Black \"?\"]\n\n1. e4 *\n";

    private const string EmptyMoves =
        "[Event \"Test\"]\n[White \"?\"]\n[Black \"?\"]\n[Result \"1-0\"]\n\n1-0\n";

    [Fact]
    public void ReviewGameText_FoolsMate_ReturnsCorrectMeta()
    {
        var g = ChessGameReview.ReviewGameText(FoolsMate, depth: 1);
        Assert.NotNull(g);
        Assert.Equal("Alice", g!.White);
        Assert.Equal("Bob", g.Black);
        Assert.Equal(1500, g.WhiteElo);
        Assert.Equal(1600, g.BlackElo);
        Assert.Equal(4, g.Plies);
    }

    [Fact]
    public void ReviewGameText_FoolsMate_BlackWins()
    {
        var g = ChessGameReview.ReviewGameText(FoolsMate, depth: 1)!;
        Assert.NotNull(g.Result);
        Assert.False(g.Result!.Value.IsDraw);
        Assert.Equal(1, g.Result.Value.Winner);
    }

    [Fact]
    public void ReviewGameText_FoolsMate_WhiteHasBlunders()
    {
        var g = ChessGameReview.ReviewGameText(FoolsMate, depth: 1)!;
        Assert.True(g.WhiteBlunders > 0, $"expected white blunders > 0, got {g.WhiteBlunders}");
    }

    [Fact]
    public void ReviewGameText_FoolsMate_NotACrazyWin()
    {
        var g = ChessGameReview.ReviewGameText(FoolsMate, depth: 1)!;
        Assert.False(g.CrazyWin);
    }

    [Fact]
    public void ReviewGameText_NullResult_ReturnsNull()
    {
        var g = ChessGameReview.ReviewGameText(NoResult, depth: 1);
        Assert.Null(g);
    }

    [Fact]
    public void ReviewGameText_EmptyMoves_ReturnsNull()
    {
        var g = ChessGameReview.ReviewGameText(EmptyMoves, depth: 1);
        Assert.Null(g);
    }

    [Fact]
    public void ReviewGameText_AcplIsNonNegative()
    {
        var g = ChessGameReview.ReviewGameText(FoolsMate, depth: 1)!;
        Assert.True(g.WhiteAcpl >= 0, $"ACPL should be non-negative, got {g.WhiteAcpl}");
        Assert.True(g.BlackAcpl >= 0, $"ACPL should be non-negative, got {g.BlackAcpl}");
    }

    [Fact]
    public void ReviewGameText_WorstMoves_AreSortedByLoss()
    {
        var g = ChessGameReview.ReviewGameText(FoolsMate, depth: 1)!;
        for (int i = 1; i < g.Worst.Count; i++)
            Assert.True(g.Worst[i - 1].CpLoss >= g.Worst[i].CpLoss,
                $"Worst[{i - 1}].CpLoss={g.Worst[i - 1].CpLoss} < Worst[{i}].CpLoss={g.Worst[i].CpLoss}");
    }

    [Fact]
    public void ReviewFile_WithDirectory_ReturnsEmpty_OnMissingDir()
    {
        var games = ChessGameReview.ReviewFile(@"C:\does\not\exist", depth: 1, maxGames: 10);
        Assert.Empty(games);
    }

    [Fact]
    public void WinnerDownCp_IsZeroForDraw()
    {
        const string drawGame =
            "[Event \"Test\"]\n[White \"A\"]\n[Black \"B\"]\n[Result \"1/2-1/2\"]\n\n" +
            "1. e4 e5 2. Nf3 Nf6 3. Nxe5 d6 4. Nf3 Nxe4 5. d4 d5 6. Bd3 Be7 " +
            "7. O-O Nc6 8. c4 Nb4 9. Be2 O-O 1/2-1/2\n";
        var g = ChessGameReview.ReviewGameText(drawGame, depth: 1);
        if (g is not null)
            Assert.Equal(0, g.WinnerDownCp);
    }
}
