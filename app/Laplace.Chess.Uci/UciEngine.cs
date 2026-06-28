using System.Linq;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Uci;

/// <summary>
/// A minimal UCI engine over the pure <see cref="Search"/> — enough of the protocol for cutechess-cli
/// (the self-play Elo ladder) and lichess-bot (online play): <c>uci / isready / ucinewgame / position /
/// go / quit</c>. Stateless w.r.t. I/O — <see cref="Handle"/> processes one command line and writes any
/// response to a supplied writer, so the whole protocol is unit-testable without stdin/stdout.
/// </summary>
public sealed class UciEngine
{
    public const string Name   = "Laplace";
    public const string Author = "Laplace";

    private Board _board = Board.FromFen(ChessModality.StartFen);
    private readonly Search _search = new();

    /// <summary>Handle one UCI command; write any response to <paramref name="output"/>. Returns false on
    /// <c>quit</c> (caller should stop the read loop).</summary>
    public bool Handle(string line, TextWriter output)
    {
        var tok = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0) return true;

        switch (tok[0])
        {
            case "uci":
                output.WriteLine($"id name {Name}");
                output.WriteLine($"id author {Author}");
                output.WriteLine("uciok");
                return true;

            case "isready":
                output.WriteLine("readyok");
                return true;

            case "ucinewgame":
                _board = Board.FromFen(ChessModality.StartFen);
                return true;

            case "position":
                SetPosition(tok);
                return true;

            case "go":
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = _search.Think(_board, ParseGo(tok));
                sw.Stop();
                string best = result.BestMove?.ToUci()
                    ?? (MoveGen.Legal(_board) is { Count: > 0 } l ? l[0].ToUci() : "0000");
                output.WriteLine(
                    $"info depth {result.Depth} score {ScoreStr(result.Score)} " +
                    $"nodes {result.Nodes} time {sw.ElapsedMilliseconds} pv {best}");
                output.WriteLine($"bestmove {best}");
                return true;
            }

            case "quit":
                return false;

            default:
                return true; // ignore the rest (setoption, debug, ponderhit, …)
        }
    }

    private void SetPosition(string[] tok)
    {
        int startIdx = Array.IndexOf(tok, "startpos");
        int fenIdx   = Array.IndexOf(tok, "fen");
        if (startIdx >= 0)
            _board = Board.FromFen(ChessModality.StartFen);
        else if (fenIdx >= 0)
            _board = Board.FromFen(string.Join(' ', tok.Skip(fenIdx + 1).Take(6))); // FEN = 6 fields

        int movesIdx = Array.IndexOf(tok, "moves");
        if (movesIdx >= 0)
            for (int k = movesIdx + 1; k < tok.Length; k++)
                ApplyUciMove(tok[k]);
    }

    // Resolve a UCI long-algebraic move (e2e4, e7e8q) against the legal moves and apply it. A GUI only
    // sends legal moves; an unrecognised one is ignored rather than crashing the engine.
    private void ApplyUciMove(string uci)
    {
        foreach (var m in MoveGen.Legal(_board))
            if (m.ToUci() == uci) { MoveApply.Make(_board, m); return; }
    }

    // UCI score: "mate N" when a forced mate is in hand (plies → full moves), else "cp".
    private static string ScoreStr(int score)
    {
        const int mate = 30_000, threshold = mate - 1_000;
        if (Math.Abs(score) < threshold) return $"cp {score}";
        int pliesToMate = mate - Math.Abs(score);
        int moves = (pliesToMate + 1) / 2;
        return $"mate {(score > 0 ? moves : -moves)}";
    }

    // Translate a "go" command into search limits: explicit depth, fixed movetime, or a clock budget
    // (≈1/30 of the remaining time plus most of the increment, with a safety margin so we never flag).
    private Search.Limits ParseGo(string[] tok)
    {
        int Int(string key, int def)
        {
            int i = Array.IndexOf(tok, key);
            return i >= 0 && i + 1 < tok.Length && int.TryParse(tok[i + 1], out var v) ? v : def;
        }

        int depth = Int("depth", 0);
        if (depth > 0) return new Search.Limits(MaxDepth: Math.Clamp(depth, 1, 64));

        int movetime = Int("movetime", 0);
        if (movetime > 0) return new Search.Limits(MaxDepth: 64, MaxTimeMs: Math.Max(10, movetime - 20));

        int wtime = Int("wtime", 0), btime = Int("btime", 0), winc = Int("winc", 0), binc = Int("binc", 0);
        if (wtime > 0 || btime > 0)
        {
            int myTime = _board.WhiteToMove ? wtime : btime;
            int myInc  = _board.WhiteToMove ? winc : binc;
            int budget = Math.Max(10, Math.Min(myTime - 30, myTime / 30 + (int)(myInc * 0.8)));
            return new Search.Limits(MaxDepth: 64, MaxTimeMs: budget);
        }

        return new Search.Limits(MaxDepth: 64, MaxNodes: 1_000_000, MaxTimeMs: 2000); // bare "go"
    }
}
