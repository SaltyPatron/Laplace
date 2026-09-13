using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class CutechessGauntletValidityTests
{
    private static readonly CutechessOptions Paired = new()
    {
        Rounds = 4,
        SecondsPerMove = 1,
        StockfishElo = 2000,
        PgnOut = "/tmp/games.pgn",
    };

    [Fact]
    public void DefaultArguments_PinSubstrate_AndUseColorSwappedOpeningPairs()
    {
        var args = CutechessRunner.BuildArguments(Paired, "/bin/laplace-uci", "/bin/stockfish").ToList();

        Assert.Contains("option.Substrate=substrate", args);
        int openings = args.IndexOf("-openings");
        Assert.True(openings >= 0);
        Assert.Equal("file=/tmp/openings.epd", args[openings + 1]);
        Assert.Equal("format=epd", args[openings + 2]);
        Assert.Equal("order=sequential", args[openings + 3]);
        int repeat = args.IndexOf("-repeat");
        Assert.True(repeat >= 0);
        Assert.Equal("2", args[repeat + 1]);
    }

    [Fact]
    public void UnpairedScheduleMustBeExplicit()
    {
        var args = CutechessRunner.BuildArguments(
            Paired with { PairOpenings = false }, "/bin/laplace-uci", "/bin/stockfish");

        Assert.DoesNotContain("-openings", args);
        Assert.DoesNotContain("-repeat", args);
    }

    [Fact]
    public void IdentityVerificationFailsClosedOnWrongOpponent()
    {
        var parser = new CutechessRunner.TranscriptParser(rounds: 2, requireEngineIdentity: true);
        _ = parser.Line(ChessLabStream.Stdout, "10 <Laplace(0): id name Laplace").ToList();
        _ = parser.Line(ChessLabStream.Stdout, "11 <Stockfish(1): id name OtherEngine").ToList();
        _ = parser.Line(ChessLabStream.Stdout, "Score of Laplace vs Stockfish: 1 - 1 - 0").ToList();

        var done = parser.Complete(0);
        Assert.Equal(ChessLabJobState.Failed, done.FinalState);
        Assert.Contains("expected Stockfish", done.Message);
    }

    [Fact]
    public void PairedScheduleFailsClosedWhenColorsDoNotSwap()
    {
        var parser = new CutechessRunner.TranscriptParser(rounds: 2, requirePairedSchedule: true);
        _ = parser.Line(ChessLabStream.Stdout, "Started game 1 of 2 (Laplace vs Stockfish)").ToList();
        _ = parser.Line(ChessLabStream.Stdout, "Started game 2 of 2 (Laplace vs Stockfish)").ToList();
        _ = parser.Line(ChessLabStream.Stdout, "Score of Laplace vs Stockfish: 1 - 1 - 0").ToList();

        var done = parser.Complete(0);
        Assert.Equal(ChessLabJobState.Failed, done.FinalState);
        Assert.Contains("did not swap colours", done.Message);
    }

    [Fact]
    public void ValidPairedRunRequiresBothIdentitiesAndSwappedColors()
    {
        var parser = new CutechessRunner.TranscriptParser(
            rounds: 2, requireEngineIdentity: true, requirePairedSchedule: true);
        _ = parser.Line(ChessLabStream.Stdout, "10 <Laplace(0): id name Laplace").ToList();
        _ = parser.Line(ChessLabStream.Stdout, "11 <Stockfish(1): id name Stockfish 18").ToList();
        _ = parser.Line(ChessLabStream.Stdout, "Started game 1 of 2 (Laplace vs Stockfish)").ToList();
        _ = parser.Line(ChessLabStream.Stdout, "Started game 2 of 2 (Stockfish vs Laplace)").ToList();
        _ = parser.Line(ChessLabStream.Stdout, "Score of Laplace vs Stockfish: 1 - 1 - 0").ToList();

        Assert.Equal(ChessLabJobState.Completed, parser.Complete(0).FinalState);
    }
}
