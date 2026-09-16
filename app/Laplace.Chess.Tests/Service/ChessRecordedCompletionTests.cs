using System.Text.Json;
using Laplace.Chess.Service;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessRecordedCompletionTests
{
    private const string Mate = "1. f3 e5 2. g4 Qh4# 0-1";
    private const string Repetition = "1. Nf3 Nf6 2. Ng1 Ng8 3. Nf3 Nf6 4. Ng1 Ng8";
    private const string MateSetup = "6k1/5ppp/8/8/8/8/8/R5K1 w - - 99 50";

    private static string Pgn(string moves, string result, string extra = "") =>
        $"[Event \"chess-lab/cutechess/completion\"]\n[White \"White\"]\n[Black \"Black\"]\n"
        + $"[Result \"{result}\"]\n{extra}\n{moves}";

    private static ChessRecordingMeasurement Measurement(string result) =>
        ChessRecordingMeasurement.FromRetainedMatch("completion", JsonSerializer.Serialize(new
        {
            experimentId = "completion", matchState = "Completed", artifactIdentitiesUnchanged = true,
            games = new[] { new { index = 1, white = "White", black = "Black", result } },
            command = new { arguments = new[] { "tc=inf", "depth=4" } }
        }));

    [Fact]
    public void ClaimedMateCannotAcceptAnUnverifiedLegalPrefix()
    {
        var game = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(Pgn("1. e4 1-0", "1-0")));
        Assert.Single(game.MoveIds);
        Assert.Throws<InvalidDataException>(() => Measurement("1-0 (White mates)").ObserveParsed(game));
    }

    [Theory]
    [InlineData("1. e4 1-0", "1-0")]
    [InlineData("1. e4 e5 1/2-1/2", "1/2-1/2")]
    [InlineData("1. f3 e5 2. g4 Qh4# 1-0", "1-0")]
    [InlineData(Repetition + " 5. e4 1/2-1/2", "1/2-1/2")]
    [InlineData("1. e5 1-0", "1-0")]
    [InlineData("1. e4 *", "*")]
    public void StrictNativeParsedReplayRejectsUnfinishedFalseOrPostTerminalResult(string moves, string result)
        => Assert.Throws<InvalidDataException>(() => ChessPgnDecomposer.TryParseGame(
            Pgn(moves, result), requireNormalCompletion: true));

    [Theory]
    [InlineData(Mate, "0-1", "0-1 (Black mates)", 4)]
    [InlineData(Repetition + " 1/2-1/2", "1/2-1/2", "1/2-1/2 (Draw by 3-fold repetition)", 8)]
    public void ExactTerminalSerializedGamesRetainNativeIdentityAndHistory(
        string moves, string result, string observed, int plies)
    {
        string pgn = Pgn(moves, result, $"[PlyCount \"{plies}\"]\n");
        var ordinary = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn));
        var verified = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn, requireNormalCompletion: true));
        Assert.Equal(ordinary.PlayingId, verified.PlayingId);
        Assert.Equal(ordinary.LineId, verified.LineId);
        Assert.Equal(ordinary.MoveIds, verified.MoveIds);
        Assert.Equal(ordinary.PositionIds, verified.PositionIds);
        Assert.True(verified.NormalCompletionVerified);
        var measurement = Measurement(observed);
        measurement.ObserveParsed(verified);
        Assert.Equal(1, measurement.ParsedGames);
    }

    [Fact]
    public void NormalMeasurementRefusesPlyCountThatDiffersFromSerializedGame()
    {
        string malformed = Pgn(Mate, "0-1", "[PlyCount \"12\"]\n");
        Assert.Throws<InvalidDataException>(() =>
            ChessPgnDecomposer.TryParseGame(malformed, requireNormalCompletion: true));

        // The parser now rejects this mismatch first. Retain an independent
        // counterexample for the measurement boundary if a caller alters its text.
        var verified = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(
            Pgn(Mate, "0-1", "[PlyCount \"4\"]\n"), requireNormalCompletion: true));
        var altered = verified with { GameText = malformed };
        Assert.Throws<InvalidDataException>(() => Measurement("0-1 (Black mates)").ObserveParsed(altered));
    }

    [Fact]
    public void OrdinaryResignationRemainsSourceTestimony()
    {
        var game = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(Pgn("1. e4 1-0", "1-0")));
        var measurement = Measurement("1-0 (Black resigns)");
        Assert.False(measurement.RequiresNormalCompletion);
        measurement.ObserveParsed(game);
        Assert.Equal(1, measurement.ParsedGames);
    }

    [Fact]
    public void TerminalSetupCannotSupplyAZeroMoveNormalGame()
        => Assert.Throws<InvalidDataException>(() => ChessPgnDecomposer.TryParseGame(
            Pgn("0-1", "0-1", "[SetUp \"1\"]\n[FEN \"rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 0 1\"]\n"),
            requireNormalCompletion: true));

    [Fact]
    public void LegalSetupQuietMateTakesPrecedenceAtHalfmoveOneHundred()
    {
        // FIDE 5.1.1 ends the game immediately on legal mate. Its explicit
        // 75-move precedence clause is 9.6.2; the 50-move rule 9.3 needs a claim.
        // https://handbook.fide.com/chapter/E012023
        var modality = new ChessModality();
        var start = modality.FromFen(MateSetup);
        Assert.Null(modality.Terminal(start));
        var move = Assert.Single(modality.LegalActions(start), m => m.ToUci() == "a1a8");
        var end = modality.Apply(start, move);
        Assert.Equal(100, end.Board.HalfmoveClock);
        Assert.Equal(GameOutcome.WonBy(0), modality.Terminal(end));

        var game = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(
            Pgn("50. Ra8# 1-0", "1-0", $"[SetUp \"1\"]\n[FEN \"{MateSetup}\"]\n"),
            requireNormalCompletion: true));
        Measurement("1-0 (White mates)").ObserveParsed(game);
    }

    [Fact]
    public void SameColorPromotedBishopsAgainstBareKingAreADeadPosition()
    {
        const string fen = "8/7k/8/8/8/n7/1B6/2B4K w - - 0 1";
        var modality = new ChessModality();
        var start = modality.FromFen(fen);
        Assert.Null(modality.Terminal(start));
        var move = Assert.Single(modality.LegalActions(start), m => m.ToUci() == "b2a3");
        Assert.Equal(GameOutcome.Draw, modality.Terminal(modality.Apply(start, move)));
        var game = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(
            Pgn("1. Bxa3 1/2-1/2", "1/2-1/2", $"[SetUp \"1\"]\n[FEN \"{fen}\"]\n"),
            requireNormalCompletion: true));
        var measurement = Measurement("1/2-1/2 (Draw by insufficient mating material)");
        Assert.True(measurement.RequiresNormalCompletion);
        measurement.ObserveParsed(game);
    }

    [Fact]
    public void NormalSetupRequiresItsExplicitSetupTag()
        => Assert.Throws<InvalidDataException>(() => ChessPgnDecomposer.TryParseGame(
            Pgn(Mate, "0-1", $"[FEN \"{MateSetup}\"]\n"), requireNormalCompletion: true));

    [Fact]
    public void NativeSyntaxRecoveryCannotCertifyNormalCompletion()
        => Assert.Throws<InvalidDataException>(() => ChessPgnDecomposer.TryParseGame(
            Pgn("1. f3 e5 2. g4 Qh4# ???garbage??? 0-1", "0-1"), requireNormalCompletion: true));

    [Fact]
    public void SeparateNativeGamesCannotBeJoinedIntoOneCompletedTrajectory()
        => Assert.Throws<InvalidDataException>(() => ChessPgnDecomposer.TryParseGame(
            Pgn("1. f3 e5 0-1\n2. g4 Qh4# 0-1", "0-1", "[PlyCount \"4\"]\n"),
            requireNormalCompletion: true));

    [Fact]
    public void SetupDeclarationCannotSilentlyFallBackToTheInitialPosition()
        => Assert.Throws<InvalidDataException>(() => ChessPgnDecomposer.TryParseGame(
            Pgn(Mate, "0-1", "[SetUp \"1\"]\n"), requireNormalCompletion: true));

    [Fact]
    public void PuzzleWithoutOpponentKingCannotCertifyNormalStalemate()
        => Assert.Throws<InvalidDataException>(() => ChessPgnDecomposer.TryParseGame(
            Pgn("1. Qa3 1/2-1/2", "1/2-1/2", "[SetUp \"1\"]\n[FEN \"8/8/8/8/8/8/Q7/7K w - - 0 1\"]\n"),
            requireNormalCompletion: true));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ActualCompleteCuteChessFixtureKeepsAllOneHundredFiftyFourPlies(int fixture)
    {
        string pgn = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures",
            $"position-playing-game-{fixture}.pgn"));
        var ordinary = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn));
        var verified = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn, requireNormalCompletion: true));
        Assert.Equal(154, verified.MoveIds.Length);
        Assert.Equal(GameOutcome.WonBy(1), verified.Result);
        Assert.Equal(ordinary.PlayingId, verified.PlayingId);
        Assert.Equal(ordinary.LineId, verified.LineId);
        Assert.Equal(ordinary.MoveIds, verified.MoveIds);
        Assert.Equal(ordinary.PositionIds, verified.PositionIds);
        Assert.True(verified.NormalCompletionVerified);
    }
}
