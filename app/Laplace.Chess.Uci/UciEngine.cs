using System.Linq;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Uci;

public sealed class UciEngine
{
    public const string Name   = "Laplace";
    public const string Author = "Laplace";

    private Board _board = Board.FromFen(ChessModality.StartFen);
    private readonly Search _search = new();

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
                return true;
        }
    }

    private void SetPosition(string[] tok)
    {
        int startIdx = Array.IndexOf(tok, "startpos");
        int fenIdx   = Array.IndexOf(tok, "fen");
        if (startIdx >= 0)
            _board = Board.FromFen(ChessModality.StartFen);
        else if (fenIdx >= 0)
            _board = Board.FromFen(string.Join(' ', tok.Skip(fenIdx + 1).Take(6)));

        int movesIdx = Array.IndexOf(tok, "moves");
        if (movesIdx >= 0)
            for (int k = movesIdx + 1; k < tok.Length; k++)
                ApplyUciMove(tok[k]);
    }

    private void ApplyUciMove(string uci)
    {
        foreach (var m in MoveGen.Legal(_board))
            if (m.ToUci() == uci) { MoveApply.Make(_board, m); return; }
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

        return new Search.Limits(MaxDepth: 64, MaxNodes: 1_000_000, MaxTimeMs: 2000);
    }
}
