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
    public void BareGo_FromStart_ReturnsLegalOpeningMove()
    {
        var outp = Run("position startpos", "go");
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
    public void MalformedFen_DoesNotThrow_AndKeepsPriorPosition()
    {
        var engine = new UciEngine();
        var sw = new StringWriter();
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
    public void Stop_DuringUnboundedDepthSearch_ReturnsWellBeforeTheSafetyCeiling()
    {
        var engine = new UciEngine();
        var sw = new StringWriter();
        Assert.True(engine.Handle("position startpos", sw));
        // depth 64 with no other time control relies entirely on "stop" (or the 120s safety net,
        // see ParseGo) to end the search — this asserts "stop" is what actually ends it, not the
        // ceiling, by requiring it to return in well under 120s.
        Assert.True(engine.Handle("go depth 64", sw));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Thread.Sleep(50);
        Assert.True(engine.Handle("stop", sw));
        elapsed.Stop();
        Assert.Contains("bestmove", sw.ToString());
        Assert.True(elapsed.ElapsedMilliseconds < 5000,
            $"stop should end an in-flight search promptly, took {elapsed.ElapsedMilliseconds}ms");
    }
}
