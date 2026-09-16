using System.Linq;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Uci.Tests;

public sealed class UciEngineTests
{

    private static string Run(params string[] commands)
    {
        var engine = new UciEngine();
        var sw = new StringWriter();
        engine.Handle("setoption name Substrate value off", sw);
        foreach (var c in commands)
            if (!engine.Handle(c, sw)) break;
        // "go" now runs the search on a background task (so real "stop" can interrupt it) instead
        // of blocking Handle() — an embedder that wants synchronous behavior, like this test
        // harness, must wait for it explicitly.
        engine.WaitForIdle();
        return sw.ToString();
    }

    [Fact]
    public void Uci_Handshake_AnnouncesIdAndUciOk()
    {
        var outp = Run("uci");
        Assert.Contains("id name Laplace", outp);
        Assert.Contains("uciok", outp);
    }

    [Fact]
    public void IsReady_AnswersReadyOk() => Assert.Contains("readyok", Run("isready"));

    [Fact]
    public void Quit_StopsTheLoop()
    {

        var outp = Run("quit", "go depth 2");
        Assert.DoesNotContain("bestmove", outp);
    }

    [Fact]
    public void StartposWithMoves_ThenGo_ReturnsLegalMove()
    {
        var outp = Run("position startpos moves e2e4 e7e5 g1f3", "go depth 3");
        string mv = BestMove(outp);


        var b = Board.FromFen(ChessModality.StartFen);
        foreach (var u in new[] { "e2e4", "e7e5", "g1f3" })
            MoveApply.Make(b, MoveGen.Legal(b).First(m => m.ToUci() == u));
        Assert.Contains(mv, MoveGen.Legal(b).Select(m => m.ToUci()));
    }

    [Fact]
    public void PositionFen_BackRankMate_Go_FindsTheMate()
    {
        var outp = Run("position fen 6k1/5ppp/8/8/8/8/8/4R1K1 w - - 0 1", "go depth 3");
        Assert.Equal("e1e8", BestMove(outp));
    }

    [Fact]
    public void RecordedGauntletTerminalOpening_ReportsMateAndNoMove()
    {
        // Retained experiment 7ca360011672441d983ec35e53b6b8e9, games 15/16:
        // Cute Chess sent this already-checkmated EPD position, then go depth 4.
        const string fen = "rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 0 1";
        var board = Board.FromFen(fen);
        Assert.True(MoveGen.InCheck(board, board.WhiteToMove));
        Assert.Empty(MoveGen.Legal(board));

        var outp = Run("debug on", "ucinewgame", "position fen " + fen, "isready", "go depth 4");
        Assert.Contains("readyok", outp);
        Assert.Contains("info depth 0 score mate 0 nodes 0", outp);
        Assert.Equal("0000", BestMove(outp));
        Assert.DoesNotContain("search failed", outp);
    }

    [Fact]
    public void BareGo_FromStart_IsStoppedExplicitlyAndReturnsLegalOpeningMove()
    {
        var engine = new UciEngine();
        using var output = new ProgressWriter();
        engine.Handle("setoption name Substrate value off", output);
        engine.Handle("position startpos", output);
        engine.Handle("go", output);
        engine.Handle("stop", output);
        engine.WaitForIdle();
        var outp = output.ToString();
        string mv = BestMove(outp);
        Assert.Contains(mv, MoveGen.Legal(Board.FromFen(ChessModality.StartFen)).Select(m => m.ToUci()));
    }

    [Fact]
    public void GoMovetime_ReturnsLegalMove_Promptly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outp = Run("position startpos", "go movetime 200");
        sw.Stop();
        string mv = BestMove(outp);
        Assert.Contains(mv, MoveGen.Legal(Board.FromFen(ChessModality.StartFen)).Select(m => m.ToUci()));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"movetime 200 should return well under 2s, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void GoWithClock_ReturnsLegalMove()
    {
        var outp = Run("position startpos moves e2e4 c7c5", "go wtime 60000 btime 60000 winc 600 binc 600");
        var b = Board.FromFen(ChessModality.StartFen);
        foreach (var u in new[] { "e2e4", "c7c5" })
            MoveApply.Make(b, MoveGen.Legal(b).First(m => m.ToUci() == u));
        Assert.Contains(BestMove(outp), MoveGen.Legal(b).Select(m => m.ToUci()));
    }

    private static string BestMove(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("bestmove"));
        return line.Split(' ')[1];
    }

    [Fact]
    public void CompletedDepthInfo_ReportsWorkInOrderBeforeBestMove()
    {
        var output = Run("position startpos", "go depth 3");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var progress = lines.Where(line => line.StartsWith("info depth ") && line.Contains(" nps "))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();

        Assert.Equal(new[] { 1, 2, 3 }, progress.Select(tokens => int.Parse(tokens[2])));
        long previousNodes = 0, previousTime = 0;
        foreach (var tokens in progress)
        {
            long nodes = long.Parse(tokens[Array.IndexOf(tokens, "nodes") + 1]);
            long time = long.Parse(tokens[Array.IndexOf(tokens, "time") + 1]);
            long nps = long.Parse(tokens[Array.IndexOf(tokens, "nps") + 1]);
            Assert.True(nodes > previousNodes);
            Assert.True(time >= previousTime);
            Assert.True(nps > 0);
            previousNodes = nodes;
            previousTime = time;
        }
        Assert.Contains("info string providers depth 1 ", output);
        Assert.Contains("info string providers depth 3 ", output);
        Assert.StartsWith("bestmove ", lines[^1]);
        Assert.Equal(BestMove(output), progress[^1][^1]);
    }

    [Fact]
    public async Task CompletedDepthInfo_IsFlushedWhileLongSearchIsStillRunning()
    {
        var engine = new UciEngine();
        using var output = new ProgressWriter();
        engine.Handle("setoption name Substrate value off", output);
        engine.Handle("position startpos", output);
        try
        {
            engine.Handle("go depth 64", output);
            string atFlush = await output.FirstProgressFlush.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("info depth 1 ", atFlush);
            Assert.DoesNotContain("bestmove ", atFlush);
        }
        finally
        {
            engine.Handle("stop", output);
            engine.WaitForIdle();
        }
        Assert.Contains(BestMove(output.ToString()),
            MoveGen.Legal(Board.FromFen(ChessModality.StartFen)).Select(move => move.ToUci()));
        Assert.DoesNotContain("search failed", output.ToString());
    }

    private sealed class ProgressWriter : TextWriter
    {
        private readonly StringWriter _text = new();
        private readonly object _gate = new();
        public TaskCompletionSource<string> FirstProgressFlush { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            lock (_gate) _text.WriteLine(value);
        }
        public override void Flush()
        {
            lock (_gate)
            {
                string text = _text.ToString();
                if (text.Contains("info depth 1 ")) FirstProgressFlush.TrySetResult(text);
            }
        }
        public override string ToString()
        {
            lock (_gate) return _text.ToString();
        }
    }

    [Fact]
    public void MalformedFen_DoesNotThrow_AndKeepsPriorPosition()
    {
        var engine = new UciEngine();
        var sw = new StringWriter();
        engine.Handle("setoption name Substrate value off", sw);
        Assert.True(engine.Handle("position startpos moves e2e4", sw));
        // Previously: Board.FromFen threw FormatException here with no catch, killing the process.
        Assert.True(engine.Handle("position fen not-a-real-fen", sw));
        Assert.True(engine.Handle("isready", sw));
        Assert.Contains("readyok", sw.ToString());

        // Position should still be "after 1. e4", not reset/corrupted by the malformed command.
        sw = new StringWriter();
        Assert.True(engine.Handle("go depth 2", sw));
        engine.WaitForIdle();
        string mv = BestMove(sw.ToString());
        var b = Board.FromFen(ChessModality.StartFen);
        MoveApply.Make(b, MoveGen.Legal(b).First(m => m.ToUci() == "e2e4"));
        Assert.Contains(mv, MoveGen.Legal(b).Select(m => m.ToUci()));
    }

    [Fact]
    public void Stop_DuringDepthSearch_ReturnsPromptlyWithoutAnImplicitTimeLimit()
    {
        var engine = new UciEngine();
        var sw = new StringWriter();
        engine.Handle("setoption name Substrate value off", sw);
        Assert.True(engine.Handle("position startpos", sw));
        // No time limit accompanies depth 64. Explicit stop still owns cancellation.
        Assert.True(engine.Handle("go depth 64", sw));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Thread.Sleep(50);
        Assert.True(engine.Handle("stop", sw));
        elapsed.Stop();
        Assert.Contains("bestmove", sw.ToString());
        Assert.True(elapsed.ElapsedMilliseconds < 5000,
            $"stop should end an in-flight search promptly, took {elapsed.ElapsedMilliseconds}ms");
    }

    [Theory]
    [InlineData("go depth 8", 8, long.MaxValue, int.MaxValue)]
    [InlineData("go depth 8 movetime 75 nodes 456", 8, 456L, 75)]
    [InlineData("go nodes 456 movetime 75 depth 8", 8, 456L, 75)]
    [InlineData("go nodes 3000000000", 64, 3000000000L, int.MaxValue)]
    [InlineData("go movetime 5", 64, long.MaxValue, 5)]
    [InlineData("go movetime 0", 64, long.MaxValue, 1)]
    [InlineData("go depth 4 wtime 60000 btime 30000 winc 600 binc 100 movestogo 20", 4, long.MaxValue, 3480)]
    [InlineData("go depth 4 movetime 50 wtime 60000", 4, long.MaxValue, 50)]
    [InlineData("go depth 4 movetime 5000 wtime 30000", 4, long.MaxValue, 1000)]
    [InlineData("go wtime 0 btime 30000", 64, long.MaxValue, 1)]
    [InlineData("go infinite", 64, long.MaxValue, int.MaxValue)]
    [InlineData("go", 64, long.MaxValue, int.MaxValue)]
    [InlineData("go depth bad nodes -1 movetime 999999999999", 64, long.MaxValue, int.MaxValue)]
    public void GoLimits_PreserveAllDeclaredBounds(string command, int depth, long nodes, int milliseconds)
    {
        var limits = ParsedLimits(new UciEngine(), command);
        Assert.Equal(depth, limits.MaxDepth);
        Assert.Equal(nodes, limits.MaxNodes);
        Assert.Equal(milliseconds, limits.MaxTimeMs);
    }

    [Fact]
    public void GoLimits_UseOnlyTheMovingSidesClockAndIncrement()
    {
        var engine = new UciEngine();
        engine.Handle("position startpos moves e2e4", TextWriter.Null);
        var limits = ParsedLimits(engine,
            "go depth 6 nodes 4000 wtime 60000 btime 30000 winc 600 binc 100 movestogo 20");
        Assert.Equal(1580, limits.MaxTimeMs);
        Assert.Equal(6, limits.MaxDepth);
        Assert.Equal(4000, limits.MaxNodes);
        Assert.Equal(int.MaxValue, ParsedLimits(engine, "go depth 6 wtime 1000").MaxTimeMs);
    }

    private static Search.Limits ParsedLimits(UciEngine engine, string command)
        => (Search.Limits)typeof(UciEngine).GetMethod("ParseGo",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(engine, [command.Split(' ', StringSplitOptions.RemoveEmptyEntries)])!;

    [Fact]
    public void CombinedDepthAndNodeLimits_FinishAtTheNodeBoundOnTheWire()
    {
        var engine = new UciEngine();
        using var output = new ProgressWriter();
        engine.Handle("setoption name Substrate value off", output);
        engine.Handle("position startpos", output);
        string completed;
        try
        {
            engine.Handle("go depth 64 nodes 2048", output);
            engine.WaitForIdle(2000);
            completed = output.ToString();
        }
        finally { engine.Handle("stop", output); }
        Assert.Contains("bestmove ", completed);
        var finalInfo = completed.Split('\n').Last(line => line.StartsWith("info depth "))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.InRange(long.Parse(finalInfo[Array.IndexOf(finalInfo, "nodes") + 1]), 1, 2048);
        Assert.Contains(BestMove(completed), MoveGen.Legal(Board.FromFen(ChessModality.StartFen))
            .Select(move => move.ToUci()));
    }

    [Theory]
    [InlineData("position startpos", "go infinite depth 1", false)]
    [InlineData("position fen rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 0 1", "go infinite", true)]
    public async Task Infinite_DoesNotPublishBestMoveBeforeStopEvenWhenSearchCompletes(
        string position, string command, bool terminal)
    {
        var engine = new UciEngine();
        using var output = new ProgressWriter();
        engine.Handle("setoption name Substrate value off", output);
        engine.Handle(position, output);
        try
        {
            engine.Handle(command, output);
            if (!terminal)
                await output.FirstProgressFlush.Task.WaitAsync(TimeSpan.FromSeconds(5));
            engine.WaitForIdle(150);
            engine.Handle("isready", output);
            Assert.Contains("readyok", output.ToString());
            Assert.DoesNotContain("bestmove ", output.ToString());
        }
        finally
        {
            engine.Handle("stop", output);
            engine.WaitForIdle();
        }
        var text = output.ToString();
        Assert.Single(text.Split('\n'), line => line.StartsWith("bestmove "));
        Assert.DoesNotContain("search failed", text);
        if (terminal) Assert.Equal("0000", BestMove(text));
        else Assert.Contains(BestMove(text), MoveGen.Legal(Board.FromFen(ChessModality.StartFen))
            .Select(move => move.ToUci()));
    }

    [Fact]
    public void ImmediateInfiniteStop_AlwaysReturnsOneBestMove()
    {
        for (int i = 0; i < 12; i++)
        {
            var engine = new UciEngine();
            using var output = new ProgressWriter();
            engine.Handle("setoption name Substrate value off", output);
            engine.Handle("go infinite", output);
            engine.Handle("stop", output);
            engine.WaitForIdle();
            var text = output.ToString();
            Assert.Single(text.Split('\n'), line => line.StartsWith("bestmove "));
            Assert.Contains(BestMove(text), MoveGen.Legal(Board.FromFen(ChessModality.StartFen))
                .Select(move => move.ToUci()));
        }
    }
}
