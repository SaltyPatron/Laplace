using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class CutechessRunnerTests
{
    [Fact]
    public void ParseLines_ExtractsScoreAndElo()
    {
        var lines = new[]
        {
            "Score of Laplace vs Stockfish: 6 - 2 - 2",
            "Elo difference: 42.5 +/- 12.3",
        };
        var events = CutechessRunner.ParseLinesForTest(lines).ToList();
        Assert.Contains(events, e => e is ChessLabProgressEvent);
        Assert.Contains(events, e => e is ChessLabMetricEvent m && m.Name == "elo_diff" && m.Value > 40);
    }

    [Fact]
    public void ParseLines_DebugPositionTraffic_EmitsBoardEvents_NotLogs()
    {
        var lines = new[]
        {
            "Started game 1 of 10 (Laplace vs Stockfish)",
            "1 >Laplace(0): position startpos",
            "2 >Laplace(0): go movetime 1000",
            "1005 <Laplace(0): bestmove e2e4",
            "1006 >Stockfish(1): position startpos moves e2e4",
            "2010 <Stockfish(1): bestmove e7e5",
            "2011 >Laplace(0): position startpos moves e2e4 e7e5",
        };
        var events = CutechessRunner.ParseLinesForTest(lines).ToList();

        var boards = events.OfType<ChessLabBoardEvent>().ToList();
        Assert.Equal(2, boards.Count);
        Assert.Equal(("e2e4", 1, 1), (boards[0].Uci, boards[0].Ply, boards[0].Game));
        Assert.StartsWith("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b", boards[0].Fen);
        Assert.Equal(("e7e5", 2), (boards[1].Uci, boards[1].Ply));
        Assert.StartsWith("rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w", boards[1].Fen);
        Assert.Equal("Laplace", boards[0].White);
        Assert.Equal("Stockfish", boards[0].Black);

        // Raw UCI traffic must never surface as log events (it would flood the SSE stream).
        Assert.DoesNotContain(events, e => e is ChessLabLogEvent log && log.Message.Contains("bestmove"));
    }

    [Fact]
    public void ParseLines_NewGame_ResetsBoard()
    {
        var lines = new[]
        {
            "Started game 1 of 2 (Laplace vs Stockfish)",
            "1 >Laplace(0): position startpos moves e2e4",
            "Started game 2 of 2 (Stockfish vs Laplace)",
            "2 >Stockfish(1): position startpos moves d2d4",
        };
        var boards = CutechessRunner.ParseLinesForTest(lines).OfType<ChessLabBoardEvent>().ToList();
        Assert.Equal(2, boards.Count);
        Assert.Equal((1, "e2e4"), (boards[0].Game, boards[0].Uci));
        Assert.Equal((2, "d2d4", 1), (boards[1].Game, boards[1].Uci, boards[1].Ply));
        Assert.Equal("Stockfish", boards[1].White);
    }
}
