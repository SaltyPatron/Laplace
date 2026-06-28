using System.Diagnostics;
using System.Linq;

namespace Laplace.Modality.Chess;

/// <summary>
/// A root-only guidance hook. Given the root position and its legal moves, returns a centipawn bonus per
/// move to blend into the search's root scoring. This is the seam where the SUBSTRATE injects its learned
/// per-position value — probed by content hash (same content = same id = an indexed/cached lookup) — WITHOUT
/// the search ever touching a database in its hot loop: the bias is computed ONCE, at the root, before the
/// tree search descends. A null bias is the pure classical engine; a substrate-backed bias is "the brain".
/// </summary>
public interface IRootBias
{
    int[] Bonus(Board root, IReadOnlyList<ChessMove> moves);
}

/// <summary>
/// Negamax alpha-beta search with quiescence, iterative deepening, a Zobrist transposition table, and
/// move ordering (TT move → MVV-LVA captures → killers). Pure C#, no DB/native — it judges leaves with
/// the classical <see cref="Evaluation"/> (centipawns, side-to-move relative). This is the tactical floor:
/// it never hangs to a forced mate the depth can see, and quiescence stops it grabbing defended material.
///
/// <para>The substrate blends in ABOVE this (opening book, root move ordering by learned eff_mu, the
/// per-overlay attestations) — read-only, never replacing the deterministic search.</para>
/// </summary>
public sealed class Search
{
    public readonly record struct Result(ChessMove? BestMove, int Score, int Depth, long Nodes);

    /// <param name="MaxDepth">Iterative-deepening ceiling (plies).</param>
    /// <param name="MaxNodes">Hard node budget; the last fully-completed iteration's move is returned.</param>
    public sealed record Limits(int MaxDepth = 6, long MaxNodes = long.MaxValue, int MaxTimeMs = int.MaxValue);

    private const int Inf           = 1_000_000;
    private const int Mate          = 30_000;
    private const int MateThreshold = Mate - 1_000; // |score| above this ⇒ a forced mate is in hand

    private const byte FlagExact = 0, FlagLower = 1, FlagUpper = 2;

    private struct TtEntry
    {
        public ulong Key;
        public int Score;
        public short Depth;
        public byte Flag;
        public bool Valid;
        public ChessMove Move;
    }

    private readonly TtEntry[] _tt;                // sized by the ttBits constructor arg
    private readonly ulong _ttMask;

    private const int MaxPly = 128;
    private readonly ChessMove[,] _killers = new ChessMove[MaxPly, 2];
    private readonly List<ulong> _path = new(MaxPly);

    private long _nodes, _maxNodes, _deadlineMs;
    private bool _aborted;
    private ChessMove _rootBestMove;
    private readonly Stopwatch _sw = new();

    // Poll the clock only every 2048 nodes (cheap), and never abort before depth 1 has produced a root
    // move — so the search always returns a legal move, even under a near-zero time budget.
    private bool TimeUp() => (_nodes & 2047) == 0 && _sw.ElapsedMilliseconds >= _deadlineMs && _rootBestMove != default;

    private readonly EvalTerm _terms;
    private readonly IRootBias? _rootBias;

    /// <summary>Create a search whose leaf evaluation uses only the given overlay <paramref name="terms"/>
    /// (default: all), optionally biased at the root by <paramref name="rootBias"/> (the substrate seam).
    /// A SUBSET eval is how the ablation ladder measures the raw Elo value of each overlay; the root bias is
    /// how the substrate's learned per-position value is injected without a DB hit in the hot loop.</summary>
    public Search(EvalTerm terms = EvalTerm.All, IRootBias? rootBias = null, int ttBits = 20)
    {
        _terms = terms;
        _rootBias = rootBias;
        int bits = Math.Clamp(ttBits, 10, 24); // 1K..16M entries — smaller for many parallel match games
        _tt = new TtEntry[1 << bits];
        _ttMask = (1UL << bits) - 1;
    }

    /// <summary>Search <paramref name="board"/> and return the best move + score (mover-relative cp, or a
    /// mate score). Iterative deepening to <paramref name="limits"/>.MaxDepth.</summary>
    public Result Think(Board board, Limits limits)
    {
        _nodes = 0;
        _maxNodes = limits.MaxNodes;
        _deadlineMs = limits.MaxTimeMs;
        _aborted = false;
        _sw.Restart();
        Array.Clear(_tt, 0, _tt.Length); // deterministic per search

        var b = board.Clone();
        ChessMove? best = null;
        int bestScore = 0, reached = 0;

        for (int depth = 1; depth <= limits.MaxDepth; depth++)
        {
            ClearKillers();
            _path.Clear();
            int score = Negamax(b, depth, -Inf, Inf, 0);
            if (_aborted) break;                 // discard the partial iteration, keep the last complete one
            best = _rootBestMove;
            bestScore = score;
            reached = depth;
            if (Math.Abs(score) >= MateThreshold) break; // mate found — no deeper search needed
            if (_sw.ElapsedMilliseconds * 2 >= _deadlineMs) break; // next (≈4-5× costlier) depth won't finish
        }
        return new Result(best, bestScore, reached, _nodes);
    }

    private int Negamax(Board b, int depth, int alpha, int beta, int ply)
    {
        if (_nodes >= _maxNodes || TimeUp()) { _aborted = true; return 0; }
        _nodes++;

        if (ply > 0 && (b.HalfmoveClock >= 100 || IsInsufficientMaterial(b))) return 0;

        ulong key = Zobrist.Hash(b);
        if (ply > 0 && _path.Contains(key)) return 0; // repetition within the line → draw

        int alphaOrig = alpha;
        ref TtEntry e = ref _tt[key & _ttMask];
        ChessMove ttMove = default;
        if (e.Valid && e.Key == key)
        {
            ttMove = e.Move;
            if (ply > 0 && e.Depth >= depth)
            {
                if (e.Flag == FlagExact) return e.Score;
                if (e.Flag == FlagLower && e.Score >= beta) return e.Score;
                if (e.Flag == FlagUpper && e.Score <= alpha) return e.Score;
            }
        }

        if (depth <= 0) return Quiesce(b, alpha, beta, ply);

        var moves = MoveGen.Legal(b);
        if (moves.Count == 0)
            return MoveGen.InCheck(b, b.WhiteToMove) ? -(Mate - ply) : 0; // checkmate (mover loses) / stalemate

        Order(b, moves, ttMove, ply);
        // Substrate root prior (centipawns): computed once, here at the root, by content-hash lookup —
        // never inside the tree. Aligns with `moves` AFTER ordering. Null for the pure classical engine.
        int[]? rootBonus = (ply == 0 && _rootBias is not null) ? _rootBias.Bonus(b, moves) : null;

        _path.Add(key);
        int best = -Inf;
        ChessMove bestMove = moves[0];
        for (int mi = 0; mi < moves.Count; mi++)
        {
            var m = moves[mi];
            var undo = MoveApply.MakeWithUndo(b, m);
            int score = -Negamax(b, depth - 1, -beta, -alpha, ply + 1);
            MoveApply.Unmake(b, m, undo);
            if (_aborted) { _path.RemoveAt(_path.Count - 1); return 0; }

            if (rootBonus is not null) score += rootBonus[mi];
            if (score > best) { best = score; bestMove = m; }
            if (best > alpha) alpha = best;
            if (alpha >= beta) { RecordKiller(b, m, ply); break; }
        }
        _path.RemoveAt(_path.Count - 1);

        if (ply == 0) _rootBestMove = bestMove;

        byte flag = best <= alphaOrig ? FlagUpper : best >= beta ? FlagLower : FlagExact;
        e.Key = key; e.Score = best; e.Depth = (short)depth; e.Flag = flag; e.Move = bestMove; e.Valid = true;
        return best;
    }

    // Quiescence: extend the search through forcing captures/promotions until the position is "quiet", so
    // the static eval is never applied in the middle of a capture exchange. In check, search ALL evasions
    // (no stand-pat) so a checked side can't wrongly stand on its static score.
    private int Quiesce(Board b, int alpha, int beta, int ply)
    {
        if (_nodes >= _maxNodes || TimeUp()) { _aborted = true; return 0; }
        _nodes++;

        bool inCheck = MoveGen.InCheck(b, b.WhiteToMove);
        if (!inCheck)
        {
            int standPat = Evaluation.Evaluate(b, _terms);
            if (standPat >= beta) return beta;
            if (standPat > alpha) alpha = standPat;
        }

        var moves = MoveGen.Legal(b);
        if (moves.Count == 0) return inCheck ? -(Mate - ply) : 0;

        var considered = inCheck ? moves : moves.Where(m => IsCaptureOrPromo(b, m)).ToList();
        if (!inCheck && considered.Count == 0) return alpha; // quiet position

        OrderCaptures(b, considered);
        foreach (var m in considered)
        {
            var undo = MoveApply.MakeWithUndo(b, m);
            int score = -Quiesce(b, -beta, -alpha, ply + 1);
            MoveApply.Unmake(b, m, undo);
            if (_aborted) return 0;
            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }
        return alpha;
    }

    // ---- move ordering ----

    private static readonly int[] PieceValue = { 0, 100, 320, 330, 500, 900, 20000 }; // index by type 0..6

    private void Order(Board b, List<ChessMove> moves, ChessMove ttMove, int ply)
    {
        var k0 = ply < MaxPly ? _killers[ply, 0] : default;
        var k1 = ply < MaxPly ? _killers[ply, 1] : default;
        moves.Sort((x, y) => Score(b, y, ttMove, k0, k1).CompareTo(Score(b, x, ttMove, k0, k1)));
    }

    private static int Score(Board b, ChessMove m, ChessMove ttMove, ChessMove k0, ChessMove k1)
    {
        if (m == ttMove) return 1_000_000;
        var victim = b.Squares[m.To];
        if (victim != Piece.Empty)
            return 100_000 + PieceValue[Math.Abs((sbyte)victim)] * 10 - PieceValue[Math.Abs((sbyte)b.Squares[m.From])];
        if ((m.Flags & MoveFlags.EnPassant) != 0) return 100_000 + 100 * 10 - 100;
        if ((m.Flags & MoveFlags.Promotion) != 0) return 90_000 + PieceValue[Math.Abs((sbyte)m.Promotion)];
        if (m == k0 || m == k1) return 80_000;
        return 0;
    }

    private static void OrderCaptures(Board b, List<ChessMove> caps)
        => caps.Sort((x, y) => Mvv(b, y).CompareTo(Mvv(b, x)));

    private static int Mvv(Board b, ChessMove m)
    {
        var victim = b.Squares[m.To];
        int v = victim != Piece.Empty ? PieceValue[Math.Abs((sbyte)victim)] : 100; // ep / promo
        return v * 10 - PieceValue[Math.Abs((sbyte)b.Squares[m.From])];
    }

    private void RecordKiller(Board b, ChessMove m, int ply)
    {
        if (ply >= MaxPly) return;
        if (b.Squares[m.To] != Piece.Empty || (m.Flags & MoveFlags.EnPassant) != 0) return; // captures aren't killers
        if (_killers[ply, 0] == m) return;
        _killers[ply, 1] = _killers[ply, 0];
        _killers[ply, 0] = m;
    }

    private void ClearKillers() => Array.Clear(_killers, 0, _killers.Length);

    private static bool IsCaptureOrPromo(Board b, ChessMove m)
        => b.Squares[m.To] != Piece.Empty || (m.Flags & (MoveFlags.EnPassant | MoveFlags.Promotion)) != 0;

    // K vs K, or K+single-minor vs K — a hard draw the search should score 0 (no pawns/rooks/queens).
    private static bool IsInsufficientMaterial(Board b)
    {
        int minors = 0;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            switch (Board.TypeOf(b.Squares[sq]))
            {
                case Piece.WPawn: case Piece.WRook: case Piece.WQueen: return false;
                case Piece.WKnight: case Piece.WBishop: minors++; break;
            }
        }
        return minors <= 1;
    }
}
