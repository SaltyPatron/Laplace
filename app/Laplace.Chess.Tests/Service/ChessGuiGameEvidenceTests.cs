using Xunit;
using Owner = Laplace.Chess.Service.ChessGuiGameEvidence;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessGuiGameEvidenceTests
{
    private const string Headers = """
        [Event "Cute Chess GUI"]
        [Site "local"]
        [Date "2026.09.16"]
        [Round "1"]
        [White "Stockfish (official)"]
        [Black "Laplace (substrate)"]
        [TimeControl "60+1"]
        """;
    private static string Pgn(string moves, string result = "0-1", string termination = "")
        => Headers + "\n[Result \"" + result + "\"]\n"
            + (termination.Length == 0 ? "" : "[Termination \"" + termination + "\"]\n")
            + "\n" + moves + " " + result + "\n";
    private static Owner.Evidence Verify(string pgn)
        => Owner.Verify(pgn, "Stockfish (official)", "Laplace (substrate)", "60+1");

    [Fact]
    public void WholeMateUsesExistingNativeGrammarLegalReplayAndCanonicalLine()
    {
        var result = Verify(Pgn("1. f3 e5 2. g4 Qh4#"));
        Assert.Equal("passed", result.Status);
        Assert.Equal(4, result.Plies);
        Assert.Equal(new[] { "f2f3", "e7e5", "g2g4", "d8h4" }, result.MovesUci);
        Assert.True(result.BoardTerminalVerified);
        Assert.True(result.CompleteSourceVerified);
        Assert.False(result.DatabaseRecordingProven);
        var parsed = ChessPgnDecomposer.TryParseGame(Pgn("1. f3 e5 2. g4 Qh4#"),
            requireNormalCompletion: true)!;
        Assert.Equal(ChessCorpusPreparation.Id(parsed.LineId), result.LineId);
    }

    [Fact]
    public void GenuineClockResultPreservesLegalFullLineWithoutClaimingBoardMate()
    {
        var result = Verify(Pgn("1. e4 e5", termination: "time forfeit"));
        Assert.Equal(2, result.Plies);
        Assert.False(result.BoardTerminalVerified);
        Assert.True(result.CompleteSourceVerified);
        Assert.Throws<InvalidDataException>(() => Verify(Pgn("1. e4 e5")));
    }

    [Theory]
    [InlineData("adjudication")]
    [InlineData("abandoned")]
    [InlineData("stalled connection")]
    [InlineData("illegal move")]
    [InlineData("unterminated")]
    public void EngineOrCollectorFailureIsNotACompletedGame(string termination)
        => Assert.Throws<InvalidDataException>(() =>
            Verify(Pgn("1. f3 e5 2. g4 Qh4#", termination: termination)));

    [Fact]
    public void IllegalTruncatedMultipleOrMisboundSourceCannotPass()
    {
        string complete = Pgn("1. f3 e5 2. g4 Qh4#");
        foreach (string pgn in new[]
        {
            Pgn("1. e5 e5", termination: "time forfeit"),
            Pgn("1. e4 e5", "*", "time forfeit"),
            complete + complete,
            complete.Replace("Stockfish (official)", "unselected engine"),
            complete.Replace("60+1", "40/300"),
            complete.Replace("[Result", "[SetUp \"1\"]\n[FEN \"8/8/8/8/8/8/8/8 w - - 0 1\"]\n[Result")
        })
            Assert.Throws<InvalidDataException>(() => Verify(pgn));
    }
}
