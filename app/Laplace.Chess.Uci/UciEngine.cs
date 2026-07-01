using System.Linq;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Uci;

public sealed class UciEngine
{
    public const string Name = "Laplace";
    public const string Author = "Laplace";

    private Board _board = Board.FromFen(ChessModality.StartFen);
    private readonly Search _search = new();
    private readonly object _outputLock = new();
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;

    public bool Handle(string line, TextWriter output)
    {
        var tok = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0) return true;

        switch (tok[0])
        {
            case "uci":
                lock (_outputLock)
                {
                    output.WriteLine($"id name {Name}");
                    output.WriteLine($"id author {Author}");
                    output.WriteLine("uciok");
                }
                return true;

            case "isready":
                // Must answer promptly even while a search is in flight — no lock contention risk
                // here since this never blocks on _searchTask, only on the (short) output lock.
                lock (_outputLock) output.WriteLine("readyok");
                return true;

            case "ucinewgame":
                StopSearch();
                _board = Board.FromFen(ChessModality.StartFen);
                return true;

            case "position":
                StopSearch();
                SetPosition(tok);
                return true;

            case "go":
                StartSearch(ParseGo(tok), output);
                return true;

            case "stop":
                StopSearch();
                return true;

            case "quit":
                StopSearch();
                return false;

            default:
                return true;
        }
    }

    // Runs the search on a background task so "stop" (and the next "position"/"quit") can be
    // read from stdin immediately instead of blocking behind Think() — real UCI GUIs (including
    // cutechess-cli, which drives this exact path with tc=inf/depth=N, i.e. no time control at
    // all) expect "stop" to be honored promptly, not just accepted and ignored.
    private void StartSearch(Search.Limits limits, TextWriter output)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var board = Board.FromFen(_board.ToFen()); // stable snapshot; _board may be reassigned by a later "position"
        _searchTask = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = _search.Think(board, limits, cts.Token);
            sw.Stop();
            string best = result.BestMove?.ToUci()
                ?? (MoveGen.Legal(board) is { Count: > 0 } l ? l[0].ToUci() : "0000");
            lock (_outputLock)
            {
                output.WriteLine(
                    $"info depth {result.Depth} score {ScoreStr(result.Score)} " +
                    $"nodes {result.Nodes} time {sw.ElapsedMilliseconds} pv {best}");
                output.WriteLine($"bestmove {best}");
                output.Flush();
            }
        }, cts.Token);
    }

    private void StopSearch()
    {
        _searchCts?.Cancel();
        try { _searchTask?.Wait(2000); } catch { /* best-effort; don't hang the UCI loop on a stuck search */ }
    }

    /// Blocks until any in-flight "go" search has written its bestmove, or the timeout elapses.
    /// "go" itself no longer blocks (see StartSearch), so an embedder that wants synchronous
    /// request/response behavior — a test harness, a non-interactive CLI use — needs this hook.
    public void WaitForIdle(int timeoutMs = 5000)
    {
        try { _searchTask?.Wait(timeoutMs); } catch { /* best-effort */ }
    }

    private void SetPosition(string[] tok)
    {
        try
        {
            int startIdx = Array.IndexOf(tok, "startpos");
            int fenIdx = Array.IndexOf(tok, "fen");
            Board next = startIdx >= 0
                ? Board.FromFen(ChessModality.StartFen)
                : fenIdx >= 0 ? Board.FromFen(string.Join(' ', tok.Skip(fenIdx + 1).Take(6))) : _board;

            int movesIdx = Array.IndexOf(tok, "moves");
            if (movesIdx >= 0)
                for (int k = movesIdx + 1; k < tok.Length; k++)
                    ApplyUciMove(next, tok[k]);

            _board = next;
        }
        catch (FormatException)
        {
            // Malformed "position fen ..." must not crash the engine process — keep whatever
            // position was already current, same as how a real UCI engine degrades.
        }
    }

    private static void ApplyUciMove(Board board, string uci)
    {
        foreach (var m in MoveGen.Legal(board))
            if (m.ToUci() == uci) { MoveApply.Make(board, m); return; }
    }

    private static string ScoreStr(int score)
    {
        const int mate = 30_000, threshold = mate - 1_000;
        if (Math.Abs(score) < threshold) return $"cp {score}";
        int pliesToMate = mate - Math.Abs(score);
        int moves = (pliesToMate + 1) / 2;
        return $"mate {(score > 0 ? moves : -moves)}";
    }

    private Search.Limits ParseGo(string[] tok)
    {
        int Int(string key, int def)
        {
            int i = Array.IndexOf(tok, key);
            return i >= 0 && i + 1 < tok.Length && int.TryParse(tok[i + 1], out var v) ? v : def;
        }

        int depth = Int("depth", 0);
        // A bounded ceiling even for an explicit depth request — "go depth N" with no other time
        // control (e.g. cutechess-cli's tc=inf/depth=N, the exact invocation this engine is
        // actually driven by) previously left MaxTimeMs at Limits' int.MaxValue default, so a
        // pathological position could hang the process indefinitely with no way to recover.
        if (depth > 0) return new Search.Limits(MaxDepth: Math.Clamp(depth, 1, 64), MaxTimeMs: 120_000);

        int movetime = Int("movetime", 0);
        if (movetime > 0) return new Search.Limits(MaxDepth: 64, MaxTimeMs: Math.Max(10, movetime - 20));

        int wtime = Int("wtime", 0), btime = Int("btime", 0), winc = Int("winc", 0), binc = Int("binc", 0);
        if (wtime > 0 || btime > 0)
        {
            int myTime = _board.WhiteToMove ? wtime : btime;
            int myInc = _board.WhiteToMove ? winc : binc;
            int budget = Math.Max(10, Math.Min(myTime - 30, myTime / 30 + (int)(myInc * 0.8)));
            return new Search.Limits(MaxDepth: 64, MaxTimeMs: budget);
        }

        return new Search.Limits(MaxDepth: 64, MaxNodes: 1_000_000, MaxTimeMs: 2000);
    }
}
