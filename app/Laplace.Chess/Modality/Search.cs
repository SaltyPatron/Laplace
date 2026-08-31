using System.Diagnostics;
using System.Linq;

namespace Laplace.Modality.Chess;

public interface IRootBias
{
    int[] Bonus(Board root, IReadOnlyList<ChessMove> moves);
}

/// <summary>An in-memory position contribution evaluated at every search leaf.</summary>
public interface ISearchPositionEvaluator
{
    int Evaluate(Board board);

    /// <summary>
    /// Capture the evidence generation used by one complete search. Implementations backed by
    /// mutable persistent state return an immutable view here; the default is already immutable.
    /// </summary>
    ISearchPositionEvaluator PrepareSearch() => this;

    /// <summary>Changes whenever scores previously stored in a search table become stale.</summary>
    long Version => 0;
}

public readonly record struct SearchTablebaseVerdict(int Wdl, int Dtz);

public sealed class Search
{
    public readonly record struct Result(ChessMove? BestMove, int Score, int Depth, long Nodes);

    public sealed record Limits(int MaxDepth = 6, long MaxNodes = long.MaxValue, int MaxTimeMs = int.MaxValue);

    private const int Inf = 1_000_000;
    private const int Mate = 30_000;
    private const int MateThreshold = Mate - 1_000;

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

    private readonly TtEntry[] _tt;
    private readonly ulong _ttMask;

    private const int MaxPly = 128;
    private readonly ChessMove[,] _killers = new ChessMove[MaxPly, 2];
    private readonly List<ulong> _path = new(MaxPly);

    // Per-ply reusable move buffers — the fix for the allocation-bound hot path
    // (GH #607: MoveGen.Legal allocated 2 lists per node, ~484 bytes/node,
    // ~35GB per bench). Each ply owns its own pseudo+legal list so a node's move
    // list survives while it recurses into ply+1. Sized past MaxPly because
    // quiescence recurses deeper than the main search; the LegalAt guard falls
    // back to an allocating gen for the rare node beyond the buffer range.
    private const int MaxBufPly = 256;
    private readonly List<ChessMove>[] _pseudoBuf;
    private readonly List<ChessMove>[] _legalBuf;

    private long _nodes, _maxNodes, _deadlineMs;
    private bool _aborted;
    private ChessMove _rootBestMove;
    private readonly Stopwatch _sw = new();
    private CancellationToken _ct;

    private bool TimeUp()
    {
        if (_ct.IsCancellationRequested) return true;
        return (_nodes & 2047) == 0 && _rootBestMove != default
            && _sw.ElapsedMilliseconds >= _deadlineMs;
    }

    private readonly EvalTerm _terms;
    private IRootBias? _rootBias;
    private ISearchPositionEvaluator? _positionEvaluator;
    private ISearchPositionEvaluator? _activePositionEvaluator;
    private long _positionEvaluatorVersion;
    private Func<Board, SearchTablebaseVerdict?>? _tablebase;
    private int[][]? _mgPst;
    private int[][]? _egPst;
    private Dictionary<string, int>? _rootBonusByUci;

    // Root bonuses are added AFTER the child search, so sibling comparisons are only sound if
    // every move that could still win post-bonus comes back with an EXACT score. Fail-hard
    // pruning at the root window breaks that: a sibling cut at alpha returns the bound itself,
    // the bonus lands on top, and each later move "beats" the best by bonus deltas — observed
    // flipping a mate-in-1 into a king shuffle. Searching root children with alpha widened by
    // a margin that strictly dominates every bias cap (both IRootBias impls cap at ±150cp)
    // keeps candidates exact; moves failing outside the margin can never win post-bonus.
    private const int RootBiasMargin = 256;

    public Search(EvalTerm terms = EvalTerm.All, IRootBias? rootBias = null, int ttBits = 20,
        int[][]? mgPst = null, int[][]? egPst = null,
        ISearchPositionEvaluator? positionEvaluator = null,
        Func<Board, SearchTablebaseVerdict?>? tablebase = null)
    {
        _terms = terms;
        _rootBias = rootBias;
        _mgPst = mgPst;
        _egPst = egPst;
        _positionEvaluator = positionEvaluator;
        _positionEvaluatorVersion = positionEvaluator?.Version ?? 0;
        _tablebase = tablebase;
        int bits = Math.Clamp(ttBits, 10, 24);
        TtBits = bits;
        _tt = new TtEntry[1 << bits];
        _ttMask = (1UL << bits) - 1;
        _pseudoBuf = new List<ChessMove>[MaxBufPly];
        _legalBuf = new List<ChessMove>[MaxBufPly];
        for (int i = 0; i < MaxBufPly; i++)
        {
            _pseudoBuf[i] = new List<ChessMove>(64);
            _legalBuf[i] = new List<ChessMove>(48);
        }
    }

    // Buffered legal-move generation for the search hot path: fills and returns
    // this ply's reusable buffer (no allocation). Falls back to an allocating
    // gen only for quiescence nodes beyond MaxBufPly (rare). The returned list
    // is owned by this ply and valid until this ply generates again.
    private List<ChessMove> LegalAt(Board b, int ply)
    {
        if ((uint)ply >= MaxBufPly)
            return MoveGen.Legal(b);
        var legal = _legalBuf[ply];
        MoveGen.Legal(b, _pseudoBuf[ply], legal);
        return legal;
    }

    public int TtBits { get; }

    /// Swap the bias/PST configuration on an existing instance so callers can
    /// reuse the transposition table allocation (32 MB at the default 2^20
    /// entries) instead of building a fresh Search per request/ply. Stable
    /// configurations retain deterministic position results between moves;
    /// changing any evaluator invalidates those results here.
    public void Reconfigure(
        IRootBias? rootBias, int[][]? mgPst, int[][]? egPst,
        ISearchPositionEvaluator? positionEvaluator = null,
        Func<Board, SearchTablebaseVerdict?>? tablebase = null)
    {
        bool changed = !ReferenceEquals(_rootBias, rootBias)
                       || !ReferenceEquals(_mgPst, mgPst)
                       || !ReferenceEquals(_egPst, egPst)
                       || !ReferenceEquals(_positionEvaluator, positionEvaluator)
                       || !Equals(_tablebase, tablebase);
        _rootBias = rootBias;
        _mgPst = mgPst;
        _egPst = egPst;
        _positionEvaluator = positionEvaluator;
        _tablebase = tablebase;
        if (changed) Array.Clear(_tt, 0, _tt.Length);
    }

    public Result Think(Board board, Limits limits, CancellationToken ct = default)
    {
        // One immutable substrate generation for the entire tree. A completed live game can
        // advance the persistent evidence between moves, but must never change scores halfway
        // through a search. Any new generation invalidates entries calculated from the old one.
        _activePositionEvaluator = _positionEvaluator?.PrepareSearch();
        long evaluatorVersion = _activePositionEvaluator?.Version ?? 0;
        if (evaluatorVersion != _positionEvaluatorVersion)
        {
            Array.Clear(_tt, 0, _tt.Length);
            _positionEvaluatorVersion = evaluatorVersion;
        }
        _nodes = 0;
        _maxNodes = limits.MaxNodes;
        _deadlineMs = limits.MaxTimeMs;
        _aborted = false;
        _ct = ct;
        _sw.Restart();
        var b = board.Clone();
        var rootMoves = MoveGen.Legal(b);
        if (rootMoves.Count == 0)
            return new Result(null, MoveGen.InCheck(b, b.WhiteToMove) ? -Mate : 0, 0, 0);

        // A non-terminal search is total: interruption can shorten the completed depth,
        // but it cannot turn a legal position into "no move". Negamax replaces this seed
        // as soon as it examines root candidates and updates it throughout the root loop.
        _rootBestMove = rootMoves[0];
        ChessMove? best = _rootBestMove;
        int bestScore = 0, reached = 0;

        _rootBonusByUci = null;

        for (int depth = 1; depth <= limits.MaxDepth; depth++)
        {
            ClearKillers();
            _path.Clear();
            int score = Negamax(b, depth, -Inf, Inf, 0);
            if (_aborted)
            {
                best = _rootBestMove;
                bestScore = score;
                break;
            }
            best = _rootBestMove;
            bestScore = score;
            reached = depth;
            if (Math.Abs(score) >= MateThreshold) break;
            if (_sw.ElapsedMilliseconds * 2 >= _deadlineMs) break;
        }
        return new Result(best, bestScore, reached, _nodes);
    }

    /// Reconstruct the principal variation by walking transposition-table best moves from the
    /// root, validating each against the legal move list so a key collision can't emit an
    /// illegal move. Call immediately after Think on the same root board.
    public IReadOnlyList<string> ExtractPv(Board board, int maxLen = 12)
    {
        var pv = new List<string>(maxLen);
        var b = board.Clone();
        var seen = new HashSet<ulong>();
        for (int i = 0; i < maxLen; i++)
        {
            ulong key = Zobrist.Hash(b);
            if (!seen.Add(key)) break;
            ref TtEntry e = ref _tt[key & _ttMask];
            if (!e.Valid || e.Key != key || e.Move == default) break;
            var mv = e.Move;
            bool legal = false;
            foreach (var lm in MoveGen.Legal(b)) if (lm == mv) { legal = true; break; }
            if (!legal) break;
            pv.Add(mv.ToUci());
            MoveApply.MakeWithUndo(b, mv);
        }
        return pv;
    }

    private int Negamax(Board b, int depth, int alpha, int beta, int ply)
    {
        if (_ct.IsCancellationRequested) { _aborted = true; return 0; }
        if (_nodes >= _maxNodes || TimeUp()) { _aborted = true; return 0; }
        _nodes++;

        if (ply > 0 && (b.HalfmoveClock >= 100 || IsInsufficientMaterial(b))) return 0;

        // Exact tablebase truth belongs inside the tree.  The root is intentionally searched:
        // each child probe then determines which legal move preserves the best WDL result.
        if (ply > 0 && _tablebase?.Invoke(b) is { } tablebase)
        {
            int distance = Math.Min(Math.Abs(tablebase.Dtz), 1_000);
            return tablebase.Wdl switch
            {
                0 => -20_000 + distance + ply,
                1 => -10_000 + distance + ply,
                2 => 0,
                3 => 10_000 - distance - ply,
                4 => 20_000 - distance - ply,
                _ => 0,
            };
        }

        ulong key = Zobrist.Hash(b);
        if (ply > 0 && _path.Contains(key)) return 0;

        int alphaOrig = alpha;
        ref TtEntry e = ref _tt[key & _ttMask];
        ChessMove ttMove = default;
        if (e.Valid && e.Key == key)
        {
            ttMove = e.Move;
            if (ply > 0 && e.Depth >= depth)
            {
                int cached = ScoreFromTt(e.Score, ply);
                if (e.Flag == FlagExact) return cached;
                if (e.Flag == FlagLower && cached >= beta) return cached;
                if (e.Flag == FlagUpper && cached <= alpha) return cached;
            }
        }

        if (depth <= 0) return Quiesce(b, alpha, beta, ply);

        var moves = LegalAt(b, ply);
        if (moves.Count == 0)
            return MoveGen.InCheck(b, b.WhiteToMove) ? -(Mate - ply) : 0;

        if (ply == 0 && _rootBias is not null && _rootBonusByUci is null)
        {
            var bonus = _rootBias.Bonus(b, moves);
            _rootBonusByUci = new Dictionary<string, int>(moves.Count);
            for (int i = 0; i < moves.Count; i++)
                if (bonus[i] != 0) _rootBonusByUci[moves[i].ToUci()] = bonus[i];
        }
        Order(b, moves, ttMove, ply);

        _path.Add(key);
        int best = -Inf;
        ChessMove bestMove = moves[0];
        if (ply == 0) _rootBestMove = bestMove;
        for (int mi = 0; mi < moves.Count; mi++)
        {
            var m = moves[mi];
            var undo = MoveApply.MakeWithUndo(b, m);
            int windowAlpha = ply == 0 && _rootBonusByUci is not null ? alpha - RootBiasMargin : alpha;
            int score = -Negamax(b, depth - 1, -beta, -windowAlpha, ply + 1);
            MoveApply.Unmake(b, m, undo);
            if (_aborted)
            {
                if (ply == 0) _rootBestMove = bestMove;
                _path.RemoveAt(_path.Count - 1);
                return best == -Inf ? 0 : best;
            }

            // A proven mate outranks any bias nudge — bonusing it only corrupts mate distance.
            if (_rootBonusByUci is not null && ply == 0 && Math.Abs(score) < MateThreshold
                && _rootBonusByUci.TryGetValue(m.ToUci(), out int bon))
                score += bon;
            if (score > best)
            {
                best = score;
                bestMove = m;
                if (ply == 0) _rootBestMove = bestMove;
            }
            if (best > alpha) alpha = best;
            if (alpha >= beta) { RecordKiller(b, m, ply); break; }
        }
        _path.RemoveAt(_path.Count - 1);

        if (ply == 0) _rootBestMove = bestMove;

        byte flag = best <= alphaOrig ? FlagUpper : best >= beta ? FlagLower : FlagExact;
        e.Key = key; e.Score = ScoreToTt(best, ply); e.Depth = (short)depth;
        e.Flag = flag; e.Move = bestMove; e.Valid = true;
        return best;
    }

    private static int ScoreToTt(int score, int ply) => score switch
    {
        >= MateThreshold => score + ply,
        <= -MateThreshold => score - ply,
        _ => score,
    };

    private static int ScoreFromTt(int score, int ply) => score switch
    {
        >= MateThreshold => score - ply,
        <= -MateThreshold => score + ply,
        _ => score,
    };

    private int Quiesce(Board b, int alpha, int beta, int ply)
    {
        if (_ct.IsCancellationRequested) { _aborted = true; return 0; }
        if (_nodes >= _maxNodes || TimeUp()) { _aborted = true; return 0; }
        _nodes++;

        bool inCheck = MoveGen.InCheck(b, b.WhiteToMove);
        if (!inCheck)
        {
            int standPat = Evaluation.Evaluate(b, _terms, _mgPst, _egPst)
                           + (_activePositionEvaluator?.Evaluate(b) ?? 0);
            if (standPat >= beta) return beta;
            if (standPat > alpha) alpha = standPat;
        }

        var moves = LegalAt(b, ply);
        if (moves.Count == 0) return inCheck ? -(Mate - ply) : 0;

        // In-place, order-preserving compaction — Quiesce runs at every
        // horizon node, and the old Where().ToList() allocated a closure, an
        // iterator, and a list per node (millions per move at the node cap).
        var considered = moves;
        if (!inCheck)
        {
            int w = 0;
            for (int i = 0; i < moves.Count; i++)
                if (IsCaptureOrPromo(b, moves[i]))
                    moves[w++] = moves[i];
            moves.RemoveRange(w, moves.Count - w);
            if (moves.Count == 0) return alpha;
        }

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

    // Internal: See (static exchange evaluation) and the motif detectors read this same
    // table — the engine's one piece-value fact (one implementation per fact).
    internal static readonly int[] PieceValue = { 0, 100, 320, 330, 500, 900, 20000 };

    private void Order(Board b, List<ChessMove> moves, ChessMove ttMove, int ply)
    {
        var k0 = ply < MaxPly ? _killers[ply, 0] : default;
        var k1 = ply < MaxPly ? _killers[ply, 1] : default;
        moves.Sort((x, y) => OrderScore(b, y, ttMove, k0, k1, ply)
            .CompareTo(OrderScore(b, x, ttMove, k0, k1, ply)));
    }

    private int OrderScore(
        Board b, ChessMove move, ChessMove ttMove, ChessMove k0, ChessMove k1, int ply)
    {
        int score = Score(b, move, ttMove, k0, k1);
        if (ply == 0 && _rootBonusByUci?.TryGetValue(move.ToUci(), out int bonus) == true)
            score += bonus * 1_000;
        return score;
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
        int v = victim != Piece.Empty ? PieceValue[Math.Abs((sbyte)victim)] : 100;
        return v * 10 - PieceValue[Math.Abs((sbyte)b.Squares[m.From])];
    }

    private void RecordKiller(Board b, ChessMove m, int ply)
    {
        if (ply >= MaxPly) return;
        if (b.Squares[m.To] != Piece.Empty || (m.Flags & MoveFlags.EnPassant) != 0) return;
        if (_killers[ply, 0] == m) return;
        _killers[ply, 1] = _killers[ply, 0];
        _killers[ply, 0] = m;
    }

    private void ClearKillers() => Array.Clear(_killers, 0, _killers.Length);

    private static bool IsCaptureOrPromo(Board b, ChessMove m)
        => b.Squares[m.To] != Piece.Empty || (m.Flags & (MoveFlags.EnPassant | MoveFlags.Promotion)) != 0;

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
