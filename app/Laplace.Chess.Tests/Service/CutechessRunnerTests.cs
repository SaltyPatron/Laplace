using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class CutechessRunnerTests
{
    private static readonly CutechessOptions Watchable = new()
    {
        Rounds = 4,
        SecondsPerMove = 1,
        StockfishElo = 2000,
        PgnOut = "/tmp/games.pgn",
    };

    // The regression this whole file exists to prevent recurring. cutechess's MatchParser
    // turns an argument-less option into QVariant(true); upstream a70c5915 made the -debug
    // branch reject exactly that, so a bare "-debug" kills the process with
    //   Warning: Empty value for option "-debug"
    // before the first game. "all" is the only value it accepts.
    [Fact]
    public void BuildArguments_PassesDebugAll_NeverBareDebug()
    {
        var args = CutechessRunner.BuildArguments(Watchable, "/opt/laplace-uci", "/usr/games/stockfish").ToList();

        int debug = args.IndexOf("-debug");
        Assert.True(debug >= 0, "-debug is what the live board and the transcript are parsed from");
        Assert.True(debug + 1 < args.Count, "a trailing bare -debug is rejected by cutechess-cli");
        Assert.Equal("all", args[debug + 1]);
    }

    [Fact]
    public void BuildArguments_ClockedByDefault_DepthSwitchesToUnclocked()
    {
        var clocked = CutechessRunner.BuildArguments(Watchable, "uci", "sf");
        Assert.Contains("st=1", clocked);
        Assert.Contains("timemargin=2000", clocked);
        Assert.DoesNotContain("tc=inf", clocked);

        var fixedDepth = CutechessRunner.BuildArguments(Watchable with { Depth = 8 }, "uci", "sf");
        Assert.Contains("tc=inf", fixedDepth);
        Assert.Contains("depth=8", fixedDepth);
        Assert.DoesNotContain(fixedDepth, a => a.StartsWith("st=", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildArguments_EachEngineParameterIsItsOwnToken()
    {
        var args = CutechessRunner.BuildArguments(Watchable with { StockfishElo = 1800 }, "/bin/uci", "/bin/sf");

        // A single "name=X cmd=Y proto=uci" token reaches cutechess as ONE parameter whose
        // value is the rest of the string, and the engine never starts.
        Assert.DoesNotContain(args, a => a.Contains(' ') && a.Contains('='));
        Assert.Contains("cmd=/bin/uci", args);
        Assert.Contains("cmd=/bin/sf", args);
        Assert.Equal(2, args.Count(a => a == "proto=uci"));
        Assert.Contains("option.UCI_LimitStrength=true", args);
        Assert.Contains("option.UCI_Elo=1800", args);
    }

    [Fact]
    public void BuildArguments_OmitsConcurrencyAndEventUnlessAsked()
    {
        var plain = CutechessRunner.BuildArguments(Watchable, "uci", "sf");
        Assert.DoesNotContain("-concurrency", plain);
        Assert.DoesNotContain("-event", plain);

        var tagged = CutechessRunner.BuildArguments(
            Watchable with { Concurrency = 4, Event = "chess-lab/cutechess/abc" }, "uci", "sf").ToList();
        Assert.Equal("4", tagged[tagged.IndexOf("-concurrency") + 1]);
        Assert.Equal("chess-lab/cutechess/abc", tagged[tagged.IndexOf("-event") + 1]);
    }

    [Fact]
    public void CommandEvent_RendersACopyablePrompt()
    {
        var evt = new ChessLabCommandEvent(
            "/opt/laplace/bin/cutechess-cli",
            ["-engine", "cmd=/opt/laplace/laplace-uci", "-pgnout", "/tmp/lab dir/games.pgn"]);

        Assert.Equal(
            "/opt/laplace/bin/cutechess-cli -engine cmd=/opt/laplace/laplace-uci -pgnout \"/tmp/lab dir/games.pgn\"",
            evt.CommandLine);
    }

    [Fact]
    public void ParseLines_ExtractsScoreAndElo()
    {
        var lines = new[]
        {
            "Score of Laplace vs Stockfish: 6 - 2 - 2",
            "Elo difference: 42.5 +/- 12.3",
        };
        var events = CutechessRunner.ParseLinesForTest(lines).ToList();

        // cutechess prints wins - losses - draws.
        Assert.Contains(events, e => e is ChessLabMetricEvent { Name: "wins", Value: 6 });
        Assert.Contains(events, e => e is ChessLabMetricEvent { Name: "losses", Value: 2 });
        Assert.Contains(events, e => e is ChessLabMetricEvent { Name: "draws", Value: 2 });
        var progress = Assert.Single(events.OfType<ChessLabProgressEvent>());
        Assert.Equal((10, 10, "6W-2L-2D"), (progress.Done, progress.Total, progress.Label));
        Assert.Contains(events, e => e is ChessLabMetricEvent m && m.Name == "elo_diff" && m.Value > 40);
    }

    [Fact]
    public void ParseLines_InfiniteElo_IsNotReportedAsAMetric()
    {
        // A shutout prints "Elo difference: inf +/- nan" — a real line from a real run.
        var events = CutechessRunner.ParseLinesForTest(
            ["Elo difference: inf +/- nan, LOS: 84.1 %, DrawRatio: 0.0 %"]).ToList();
        Assert.DoesNotContain(events, e => e is ChessLabMetricEvent { Name: "elo_diff" });
    }

    // "-rounds n ... for two-player tournaments this option should be used to set the total
    // number of games to play" (cutechess-cli(6)). The lab used rounds x 2, so a 10-game
    // match reported progress out of 20 and could never pass 50%. The banner is authoritative.
    [Fact]
    public void ParseLines_TakesTheGameTotalFromCutechessOwnBanner()
    {
        var events = CutechessRunner.ParseLinesForTest(
        [
            "Started game 1 of 6 (Laplace vs Stockfish)",
            "Score of Laplace vs Stockfish: 1 - 0 - 0",
        ], rounds: 99).OfType<ChessLabProgressEvent>().ToList();

        Assert.Equal((0, 6, "game 1"), (events[0].Done, events[0].Total, events[0].Label));
        Assert.Equal((1, 6), (events[1].Done, events[1].Total));
    }

    [Fact]
    public void ParseLines_FinishedGame_BecomesAGameEvent()
    {
        var events = CutechessRunner.ParseLinesForTest(
            ["Finished game 3 (Laplace vs Stockfish): 1/2-1/2 {Draw by 3-fold repetition}"]).ToList();

        var game = Assert.Single(events.OfType<ChessLabGameEvent>());
        Assert.Equal(3, game.Index);
        Assert.Equal("Laplace", game.White);
        Assert.Equal("Stockfish", game.Black);
        Assert.Equal("1/2-1/2 (Draw by 3-fold repetition)", game.Result);
    }

    [Fact]
    public void ParseLines_FinishedMatch_IsNotAGameEvent()
    {
        Assert.Empty(CutechessRunner.ParseLinesForTest(["Finished match"]).OfType<ChessLabGameEvent>());
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
    public void ParseLines_UciTraffic_IsTaggedByEngineAndDirection()
    {
        var events = CutechessRunner.ParseLinesForTest(
        [
            "11 >Laplace(0): go depth 4",
            "26 <Stockfish(1): info depth 12 score cp 31",
            "Started game 1 of 10 (Laplace vs Stockfish)",
        ]).OfType<ChessLabTerminalEvent>().ToList();

        Assert.Equal(3, events.Count);
        Assert.Equal((ChessLabStream.Uci, "go depth 4", "Laplace", ChessLabDirection.Send),
            (events[0].Stream, events[0].Text, events[0].Engine, events[0].Direction));
        Assert.Equal((ChessLabStream.Uci, "info depth 12 score cp 31", "Stockfish", ChessLabDirection.Recv),
            (events[1].Stream, events[1].Text, events[1].Engine, events[1].Direction));

        // Everything cutechess says about itself stays on stdout, untagged.
        Assert.Equal(ChessLabStream.Stdout, events[2].Stream);
        Assert.Null(events[2].Engine);
    }

    [Fact]
    public void ParseStreams_Stderr_SurfacesLiveInsteadOfAsAnEpitaph()
    {
        var events = CutechessRunner.ParseStreamsForTest(
            [(ChessLabStream.Stderr, "Warning: Empty value for option \"-debug\"")]).ToList();

        var terminal = Assert.Single(events.OfType<ChessLabTerminalEvent>());
        Assert.Equal(ChessLabStream.Stderr, terminal.Stream);
        var log = Assert.Single(events.OfType<ChessLabLogEvent>());
        Assert.Equal("warning", log.Level);
    }

    [Fact]
    public void ParseLines_EloOutsideTheEnginesRange_WarnsOnce()
    {
        // Stockfish 14.1 accepts 1350-2850; 16+ accepts 1320-3190. Only the running engine
        // knows, and it says so during handshake.
        var lines = new[]
        {
            "400 <Stockfish(1): option name UCI_Elo type spin default 1350 min 1350 max 2850",
            "401 <Stockfish(1): option name UCI_Elo type spin default 1350 min 1350 max 2850",
        };

        var warned = CutechessRunner.ParseLinesForTest(lines, requestedElo: 3000)
            .OfType<ChessLabLogEvent>().Where(l => l.Level == "warning").ToList();
        var warning = Assert.Single(warned);
        Assert.Contains("1350", warning.Message);
        Assert.Contains("2850", warning.Message);

        Assert.DoesNotContain(
            CutechessRunner.ParseLinesForTest(lines, requestedElo: 2000).OfType<ChessLabLogEvent>(),
            l => l.Level == "warning");
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
