using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Laplace.Engine.Core;

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

public sealed partial class Search
{
    public readonly record struct Result(ChessMove? BestMove, int Score, int Depth, long Nodes);

    /// <summary>A completed iterative-deepening result, with cumulative search work and time.</summary>
    public readonly record struct Iteration(
        ChessMove BestMove, int Score, int Depth, long Nodes, long ElapsedMilliseconds);

    public sealed record Limits(int MaxDepth = 6, long MaxNodes = long.MaxValue, int MaxTimeMs = int.MaxValue);

    private const int Inf = 1_000_000;
    private const int Mate = 30_000;
    private const int MateThreshold = Mate - 1_000;

    // A draw is not globally good or bad. In a position already credibly winning for the root
    // side, voluntarily collapsing the line to repetition/stalemate/50-move draw is a loss of
    // utility; in a credibly losing position it is a rescue. Keep the preference deliberately
    // bounded far below material/mate/tablebase scales, with a deadband around equality so a
    // noisy +0.2 does not become artificial contempt.
    private const int DrawPreferenceDeadbandCp = 75;
    private const int DrawPreferenceCapCp = 200;

    private const byte FlagExact = 0, FlagLower = 1, FlagUpper = 2, FlagRootSteered = 3;

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

    // Real game history and speculative descendant history use the SAME canonical position id
    // and the SAME irreversible-move reset law as ChessModality.Apply. This is not the TT/Zobrist
    // path: a second occurrence is an ordinary transposition, while the third is a chess draw.
    private readonly List<Hash128> _repetitionHistory = new(MaxPly + 128);

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
    private int _drawUtilityRootCp;

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
    private SearchContinuationTable? _continuations;
    private int[][]? _mgPst;
    private int[][]? _egPst;
    private Dictionary<string, int>? _rootBonusByUci;
    private bool _rootBiasPrepared;

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
        Func<Board, SearchTablebaseVerdict?>? tablebase = null,
        SearchContinuationTable? continuations = null)
    {
        _terms = terms;
        _rootBias = rootBias;
        _mgPst = mgPst;
        _egPst = egPst;
        _positionEvaluator = positionEvaluator;
        _positionEvaluatorVersion = positionEvaluator?.Version ?? 0;
        _tablebase = tablebase;
        _continuations = continuations;
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
        Func<Board, SearchTablebaseVerdict?>? tablebase = null,
        SearchContinuationTable? continuations = null)
    {
        // Move ordering does not enter the transposition table. A witnessed-evaluation
        // seed does, because the leaf blend is part of the stored score.
        int previousEval = _continuations?.EvaluationStamp ?? 0;
        int nextEval = continuations?.EvaluationStamp ?? 0;
        bool changed = !ReferenceEquals(_rootBias, rootBias)
                       || !ReferenceEquals(_mgPst, mgPst)
                       || !ReferenceEquals(_egPst, egPst)
                       || !ReferenceEquals(_positionEvaluator, positionEvaluator)
                       || !Equals(_tablebase, tablebase)
                       || previousEval != nextEval;
        _rootBias = rootBias;
        _mgPst = mgPst;
        _egPst = egPst;
        _positionEvaluator = positionEvaluator;
        _tablebase = tablebase;
        _continuations = continuations;
        if (changed) Array.Clear(_tt, 0, _tt.Length);
    }

    /// <summary>
    /// Snapshot-only entry point. With no played trajectory available the current canonical
    /// position is the whole repetition segment. Live/connected callers should pass ChessState.
    /// </summary>
    public Result Think(Board board, Limits limits, CancellationToken ct = default,
        Action<Iteration>? onIterationCompleted = null)
    {
        var current = ChessPositionIdentity.PositionId(board);
        return ThinkCore(board, limits, [current], ct, onIterationCompleted);
    }

    /// <summary>
    /// History-bearing entry point. Search consumes the exact repetition segment already carried
    /// by ChessState and extends it under the same pawn/capture reset law for every descendant.
    /// </summary>
    public Result Think(ChessState state, Limits limits, CancellationToken ct = default,
        Action<Iteration>? onIterationCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ThinkCore(state.Board, limits, state.RepetitionHistory, ct, onIterationCompleted);
    }

    private Result ThinkCore(
        Board board,
        Limits limits,
        IReadOnlyList<Hash128> rootHistory,
        CancellationToken ct,
        Action<Iteration>? onIterationCompleted)
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

        _repetitionHistory.Clear();
        if (rootHistory.Count > 0)
            _repetitionHistory.AddRange(rootHistory);
        else
            _repetitionHistory.Add(ChessPositionIdentity.PositionId(b));
        int repetitionStart = 0;
        ulong repetitionSignature = RepetitionSignature(_repetitionHistory, repetitionStart);

        // Draw preference belongs to this decision root, not to chess ontology. Use exact
        // tablebase WDL when the root is covered; otherwise use the same classical + selected
        // substrate position state that evaluates descendant leaves. A change in this root-scoped
        // utility invalidates TT scores because a repeated/drawn node's value is contextual.
        int rootAdvantageCp = Evaluation.Evaluate(b, _terms, _mgPst, _egPst)
                              + (_activePositionEvaluator?.Evaluate(b) ?? 0);
        if (ChessModality.RuleWinner(b) is { } rootWinner)
            return new Result(null, rootWinner == (b.WhiteToMove ? 0 : 1) ? Mate : -Mate, 0, 0);
        bool rootRuleDraw = (!b.Rules.Drops && b.HalfmoveClock >= 100)
                            || IsInsufficientMaterial(b)
                            || IsThreefoldCurrent(repetitionStart);
        if (rootRuleDraw)
        {
            // Already-forced draw at the root: static material (e.g. K+B vs K) must not invent
            // contempt for an outcome chess law has already closed as a draw.
            rootAdvantageCp = 0;
        }
        else if (b.Rules.IsStandard && Men(b) <= TablebaseMen && _tablebase?.Invoke(b) is { } rootTablebase)
        {
            rootAdvantageCp = rootTablebase.Wdl switch
            {
                0 or 1 => -20_000,
                2 => 0,
                3 or 4 => 20_000,
                _ => rootAdvantageCp,
            };
        }
        int nextDrawUtility = ContextualDrawScore(rootAdvantageCp, ply: 0);
        if (nextDrawUtility != _drawUtilityRootCp)
        {
            Array.Clear(_tt, 0, _tt.Length);
            _drawUtilityRootCp = nextDrawUtility;
        }

        // These are already terminal under the exact game history. Search must not manufacture a
        // move after the game has ended merely because the Board snapshot still has legal moves.
        if (rootRuleDraw)
            return new Result(null, 0, 0, 0);

        var rootMoves = MoveGen.Legal(b);
        if (rootMoves.Count == 0)
            return new Result(null,
                MoveGen.InCheck(b, b.WhiteToMove) ? -Mate : DrawScoreAtPly(0), 0, 0);

        // A non-terminal search is total: interruption can shorten the completed depth,
        // but it cannot turn a legal position into "no move". Negamax replaces this seed
        // as soon as it examines root candidates and updates it throughout the root loop.
        _rootBestMove = rootMoves[0];
        ChessMove? best = _rootBestMove;
        int bestScore = 0, reached = 0;

        _rootBonusByUci = null;
        _rootBiasPrepared = false;

        for (int depth = 1; depth <= limits.MaxDepth; depth++)
        {
            ClearKillers();
            int score = Negamax(
                b, depth, -Inf, Inf, 0, repetitionStart, repetitionSignature);
            if (_aborted)
            {
                // A partial deeper iteration must not overwrite the move/score belonging to
                // the completed depth. With no completed iteration, retain the legal seed.
                if (reached == 0)
                {
                    best = _rootBestMove;
                    bestScore = score;
                }
                break;
            }
            best = _rootBestMove;
            bestScore = score;
            reached = depth;
            onIterationCompleted?.Invoke(new Iteration(
                _rootBestMove, score, depth, _nodes, _sw.ElapsedMilliseconds));
            if (Math.Abs(score) >= MateThreshold) break;
            if (_sw.ElapsedMilliseconds * 2 >= _deadlineMs) break;
        }
        return new Result(best, bestScore, reached, _nodes);
    }

    /// <summary>
    /// Root-contextual draw utility. Positive root advantage makes a draw negative; negative root
    /// advantage makes it positive. Negamax parity converts that root preference to the current
    /// side-to-move point of view at the draw node.
    /// </summary>
    internal static int ContextualDrawScore(int rootAdvantageCp, int ply)
    {
        long magnitudeRaw = Math.Abs((long)rootAdvantageCp) - DrawPreferenceDeadbandCp;
        if (magnitudeRaw <= 0) return 0;
        int magnitude = (int)Math.Min(DrawPreferenceCapCp, Math.Max(1L, magnitudeRaw / 2));
        int rootPov = rootAdvantageCp > 0 ? -magnitude : magnitude;
        return (ply & 1) == 0 ? rootPov : -rootPov;
    }

    private int DrawScoreAtPly(int ply)
        => (ply & 1) == 0 ? _drawUtilityRootCp : -_drawUtilityRootCp;

    /// Reconstruct the principal variation by walking transposition-table best moves from the
    /// root, validating each against the legal move list so a key collision can't emit an
    /// illegal move. Call immediately after Think on the same root state.
    public IReadOnlyList<string> ExtractPv(Board board, int maxLen = 12)
    {
        var current = ChessPositionIdentity.PositionId(board);
        return ExtractPvCore(board, [current], maxLen);
    }

    public IReadOnlyList<string> ExtractPv(ChessState state, int maxLen = 12)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ExtractPvCore(state.Board, state.RepetitionHistory, maxLen);
    }

    private IReadOnlyList<string> ExtractPvCore(
        Board board,
        IReadOnlyList<Hash128> rootHistory,
        int maxLen)
    {
        var pv = new List<string>(maxLen);
        var b = board.Clone();
        var history = rootHistory.Count > 0
            ? new List<Hash128>(rootHistory)
            : [ChessPositionIdentity.PositionId(b)];
        int repetitionStart = 0;
        ulong repetitionSignature = RepetitionSignature(history, repetitionStart);

        for (int i = 0; i < maxLen; i++)
        {
            ulong key = TtKey(b, repetitionSignature);
            ref TtEntry e = ref _tt[key & _ttMask];
            if (!e.Valid || e.Key != key || e.Move == default) break;
            var mv = e.Move;
            bool legal = false;
            foreach (var lm in MoveGen.Legal(b)) if (lm == mv) { legal = true; break; }
            if (!legal) break;

            bool reset = ResetsRepetition(b, mv);
            pv.Add(mv.ToUci());
            MoveApply.Make(b, mv);
            var childId = ChessPositionIdentity.PositionId(b);
            history.Add(childId);
            if (reset)
            {
                repetitionStart = history.Count - 1;
                repetitionSignature = RepetitionAppend(0, childId);
            }
            else
            {
                repetitionSignature = RepetitionAppend(repetitionSignature, childId);
            }
            if (CountCurrent(history, repetitionStart) >= 3) break;
        }
        return pv;
    }

    private int Negamax(
        Board b,
        int depth,
        int alpha,
        int beta,
        int ply,
        int repetitionStart,
        ulong repetitionSignature)
    {
        if (_ct.IsCancellationRequested) { _aborted = true; return 0; }
        if (_nodes >= _maxNodes || TimeUp()) { _aborted = true; return 0; }
        _nodes++;

        if (ChessModality.RuleWinner(b) is { } winner) return winner == (b.WhiteToMove ? 0 : 1) ? Mate - ply : -Mate + ply;
        if (ply > 0 && ((!b.Rules.Drops && b.HalfmoveClock >= 100)
                        || IsInsufficientMaterial(b)
                        || IsThreefoldCurrent(repetitionStart)))
            return DrawScoreAtPly(ply);

        // Exact tablebase truth belongs inside the tree. The root is intentionally searched:
        // each child probe then determines which legal move preserves the best WDL result. WDL
        // draw remains exact truth, but its decision utility depends on whether the root was
        // winning, equal or losing; exact wins/losses retain their much larger tablebase scale.
        if (ply > 0 && b.Rules.IsStandard && Men(b) <= TablebaseMen && _tablebase?.Invoke(b) is { } tablebase)
        {
            int distance = Math.Min(Math.Abs(tablebase.Dtz), 1_000);
            return tablebase.Wdl switch
            {
                0 => -20_000 + distance + ply,
                1 => -10_000 + distance + ply,
                2 => DrawScoreAtPly(ply),
                3 => 10_000 - distance - ply,
                4 => 20_000 - distance - ply,
                _ => 0,
            };
        }

        ulong key = TtKey(b, repetitionSignature);
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

        if (depth <= 0)
            return Quiesce(
                b, alpha, beta, ply, repetitionStart, repetitionSignature);

        var moves = LegalAt(b, ply);
        if (moves.Count == 0)
            return MoveGen.InCheck(b, b.WhiteToMove) ? -(Mate - ply) : DrawScoreAtPly(ply);

        if (ply == 0 && _rootBias is not null && !_rootBiasPrepared)
        {
            var bonus = _rootBias.Bonus(b, moves);
            _rootBiasPrepared = true;
            for (int i = 0; i < moves.Count; i++)
            {
                if (bonus[i] == 0) continue;
                // An observed all-zero provider is a no-op: no widened root window, root
                // steering TT flag, or UCI-key allocation in the move-ordering comparator.
                _rootBonusByUci ??= new Dictionary<string, int>(moves.Count);
                _rootBonusByUci[moves[i].ToUci()] = bonus[i];
            }
        }
        if (ply == 0)
            FilterRootTablebase(b, moves);
        ChessMove? preferred = null;
        if (_continuations is not null
            && _continuations.TryGet(ChessPositionIdentity.PositionId(b), out var continuation))
        {
            if (ply > 0)
            {
                if (continuation.IsRetrogradeSolved)
                    return RetrogradeScore(continuation, ply);
                if (continuation.CanAmputateInterior && depth >= 2 && !MoveGen.InCheck(b, b.WhiteToMove))
                    return Quiesce(b, alpha, beta, ply, repetitionStart, repetitionSignature);
                preferred = continuation.PreferredMove;
            }
            else if (continuation.IsRetrogradeSolved)
            {
                preferred = continuation.RetrogradeMove;
            }
        }
        Order(b, moves, ttMove, ply, preferred);

        int best = -Inf;
        ChessMove bestMove = moves[0];
        if (ply == 0) _rootBestMove = bestMove;
        for (int mi = 0; mi < moves.Count; mi++)
        {
            var m = moves[mi];
            bool resetRepetition = ResetsRepetition(b, m);
            var undo = MoveApply.MakeWithUndo(b, m);
            Hash128 childId = ChessPositionIdentity.PositionId(b);
            _repetitionHistory.Add(childId);
            int childRepetitionStart = resetRepetition
                ? _repetitionHistory.Count - 1
                : repetitionStart;
            ulong childRepetitionSignature = resetRepetition
                ? RepetitionAppend(0, childId)
                : RepetitionAppend(repetitionSignature, childId);

            int windowAlpha = ply == 0 && _rootBonusByUci is not null ? alpha - RootBiasMargin : alpha;
            int score = -Negamax(
                b, depth - 1, -beta, -windowAlpha, ply + 1,
                childRepetitionStart, childRepetitionSignature);

            _repetitionHistory.RemoveAt(_repetitionHistory.Count - 1);
            MoveApply.Unmake(b, m, undo);
            if (_aborted)
            {
                if (ply == 0) _rootBestMove = bestMove;
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

        if (ply == 0) _rootBestMove = bestMove;

        // Root steering answers which action this forward pass should select; it is not a
        // context-free value of the board. The same board can reappear as an interior
        // transposition in a later search. Retain the selected move for PV/order reuse, but
        // keep the steered score out of the exact/lower/upper lookup above.
        byte flag = ply == 0 && _rootBonusByUci is not null
            ? FlagRootSteered
            : best <= alphaOrig ? FlagUpper : best >= beta ? FlagLower : FlagExact;
        e.Key = key; e.Score = ScoreToTt(best, ply); e.Depth = (short)depth;
        e.Flag = flag; e.Move = bestMove; e.Valid = true;
        return best;
    }
}
