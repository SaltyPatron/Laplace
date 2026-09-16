using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessCorpusCompletionTests
{
    // All inline PGNs below are synthetic controls for parser policy, not corpus
    // acquisitions or throughput evidence. The recorded fixture is read unchanged.
    private static string Pgn(string moves, string? result = "1-0", string extra = "") =>
        "[Event \"Synthetic corpus completion control\"]\n"
        + "[Site \"test-only\"]\n[Date \"2026.09.16\"]\n[Round \"1\"]\n"
        + "[White \"Test White\"]\n[Black \"Test Black\"]\n"
        + (result is null ? "" : $"[Result \"{result}\"]\n")
        + extra + "\n" + moves + "\n";

    private static ChessGameRecord Complete(string pgn) =>
        Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn, requireCompleteSource: true));

    [Fact]
    public void RecordedFixtureRetainsEveryPlyIdentityAndOriginalTextUnderBothStrictPolicies()
    {
        string pgn = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "position-playing-game-1.pgn"));
        var ordinary = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn));
        var corpus = Complete(pgn);
        var normal = Assert.IsType<ChessGameRecord>(
            ChessPgnDecomposer.TryParseGame(pgn, requireNormalCompletion: true));

        Assert.Equal(154, corpus.MoveIds.Length);
        Assert.Equal(155, corpus.PositionIds.Length);
        Assert.Equal(pgn, corpus.GameText);
        Assert.Equal(ordinary.PlayingId, corpus.PlayingId);
        Assert.Equal(ordinary.LineId, corpus.LineId);
        Assert.Equal(ordinary.MoveIds, corpus.MoveIds);
        Assert.Equal(ordinary.PositionIds, corpus.PositionIds);
        Assert.Equal(normal.PlayingId, corpus.PlayingId);
        Assert.False(ordinary.CompleteSourceVerified);
        Assert.True(corpus.CompleteSourceVerified);
        Assert.False(corpus.NormalCompletionVerified);
        Assert.True(normal.CompleteSourceVerified);
        Assert.True(normal.NormalCompletionVerified);
    }

    [Theory]
    [InlineData("1-0", "Test White won by resignation")]
    [InlineData("1/2-1/2", "Draw by agreement")]
    public void FinishedSourceMayEndWithLegalMovesRemainingWithoutCertifyingNormalTermination(
        string result, string termination)
    {
        string pgn = Pgn($"1. e4 e5 {result}", result, $"[Termination \"{termination}\"]\n");
        var game = Complete(pgn);
        Assert.True(game.CompleteSourceVerified);
        Assert.False(game.NormalCompletionVerified);
        Assert.Equal(result, game.Result.ResultToken);
        Assert.Equal(2, game.MoveIds.Length);

        var modality = new ChessModality();
        var state = modality.FromFen(ChessModality.StartFen);
        foreach (var move in game.ResolvedMoves) state = modality.Apply(state, move);
        Assert.NotEmpty(modality.LegalActions(state));
        Assert.Null(modality.Terminal(state));
        Assert.Throws<InvalidDataException>(() =>
            ChessPgnDecomposer.TryParseGame(pgn, requireNormalCompletion: true));
    }

    [Theory]
    [InlineData("1. e4 e5 0-1", "1-0", "")]
    [InlineData("1. e4 e5 *", "*", "")]
    [InlineData("1. e4 e5", "1-0", "")]
    [InlineData("1. e4 e5 1-0", null, "")]
    [InlineData("1. e4 e5 1-0", "1-0", "[Termination \"unterminated\"]\n")]
    [InlineData("1. e4 e5 1-0", "1-0", "[Termination \"abandoned\"]\n")]
    public void FinishedResultHeaderAndTerminationMustDescribeACompleteSourceGame(
        string moves, string? result, string extra)
        => Assert.Throws<InvalidDataException>(() => Complete(Pgn(moves, result, extra)));

    [Theory]
    [InlineData("1. e4 e5 2. Bh6 1-0")]
    [InlineData("1. e4 e5 2. Nf 1-0")]
    [InlineData("1. e4 e5 {unfinished annotation 1-0")]
    [InlineData("1. e4 e5 ???garbage??? 1-0")]
    public void IllegalSanAndRecoveredOrTruncatedSyntaxCannotCertifyTheSource(string moves)
        => Assert.Throws<InvalidDataException>(() => Complete(Pgn(moves)));

    [Theory]
    [InlineData("[SetUp \"1\"]\n", "1. e4 e5 1-0")]
    [InlineData("[FEN \"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1\"]\n", "1. e4 e5 1-0")]
    [InlineData("[SetUp \"0\"]\n[FEN \"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1\"]\n", "1. e4 e5 1-0")]
    [InlineData("[SetUp \"1\"]\n[FEN \"8/8/8/8/8/8/Q7/7K w - - 0 1\"]\n", "1. Qa3 1-0")]
    [InlineData("[SetUp \"1\"]\n[FEN \"7k/8/8/8/8/8/Q6K/7K w - - 0 1\"]\n", "1. Qa3 1-0")]
    public void SetupDeclarationsAndKingCountsCannotBeRepairedSilently(string extra, string moves)
        => Assert.Throws<InvalidDataException>(() => Complete(Pgn(moves, extra: extra)));

    [Theory]
    [InlineData("1. f3 e5 2. g4 Qh4#", "", "0-1", "1-0")]
    [InlineData("1. Qb6", "[SetUp \"1\"]\n[FEN \"k7/2Q5/2K5/8/8/8/8/8 w - - 0 1\"]\n", "1/2-1/2", "1-0")]
    public void ForcedMateAndStalemateMustAgreeWithTheSourceResult(
        string moves, string extra, string correct, string contradicted)
    {
        var game = Complete(Pgn(moves + " " + correct, correct, extra));
        Assert.Equal(correct, game.Result.ResultToken);
        Assert.True(game.CompleteSourceVerified);
        Assert.Throws<InvalidDataException>(() =>
            Complete(Pgn(moves + " " + contradicted, contradicted, extra)));
    }

    [Fact]
    public void TwoNativeGamesCannotBeCertifiedAsOneSourceOccurrence()
    {
        string first = Pgn("1. e4 e5 1-0");
        string second = Pgn("1. d4 d5 1/2-1/2", "1/2-1/2");
        Assert.Throws<InvalidDataException>(() => Complete(first + "\n" + second));
    }

    [Theory]
    [InlineData("1. Nf3 Nf6 2. Ng1 Ng8 3. Nf3 Nf6 4. Ng1 Ng8 5. e4 1-0", "", 9)]
    [InlineData("50. Ra2 h6 1-0", "[SetUp \"1\"]\n[FEN \"6k1/5ppp/8/8/8/8/8/R5K1 w - - 99 50\"]\n", 2)]
    public void ClaimableDrawDoesNotEraseLegalContinuationInAnAuthenticSourcePolicy(
        string moves, string extra, int plies)
    {
        string pgn = Pgn(moves, extra: extra);
        var game = Complete(pgn);
        Assert.Equal(plies, game.MoveIds.Length);
        Assert.Equal(plies + 1, game.PositionIds.Length);
        Assert.True(game.CompleteSourceVerified);
        Assert.False(game.NormalCompletionVerified);
        Assert.Throws<InvalidDataException>(() =>
            ChessPgnDecomposer.TryParseGame(pgn, requireNormalCompletion: true));
    }
}
