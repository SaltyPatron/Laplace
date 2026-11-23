using global::Npgsql;
using Laplace.Data;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Chess.Service;

public readonly record struct ChessMoveScore(string Uci, double EffMu, bool Rated);

public sealed record ChessLegalResponse(
    IReadOnlyList<ChessMoveScore> Moves,
    bool InCheck,
    string Status);

public sealed record ChessBestMove(
    string? Uci, string Fen, double EffMu, bool Rated, bool Terminal, string Status,
    int ScoreCp = 0, int Depth = 0, long Nodes = 0,
    IReadOnlyList<string>? Pv = null, IReadOnlyList<string>? Motifs = null,
    int SyzygyWdl = 0, bool FromSyzygy = false, long WallUs = 0,
    string? Source = null, string? BestSan = null);

public sealed record ChessPositionEval(
    int WhiteScoreCp, int Depth, long Nodes, bool Substrate, bool Terminal, string Status,
    int SyzygyWdl = 0, bool FromSyzygy = false, long WallUs = 0, string? Source = null);

public sealed record ChessApplyResult(
    string Fen, bool Terminal, string Status, bool Legal, IReadOnlyList<string>? Motifs = null);

public sealed record ChessTrainStatus(
    bool Running, long Games, int White, int Black, int Draws, int Adjudicated,
    string LastOutcome, double Temperature, double Weight, int MaxPlies);

public sealed record ChessExploreMove(
    string Uci, string San, double EffMu, double Rd, long Witnesses);

public sealed record ChessExploreResponse(
    string Fen, IReadOnlyList<ChessExploreMove> Moves);

public sealed record ChessPlayStart(Guid SessionId, string Fen, string Status = "ongoing", int Ply = 0,
    string TenantId = "public", string? UserId = null, string GameId = "", OperationPrincipal? Requester = null);

public sealed record ChessPlayMoveResult(
    string Fen, bool Terminal, string Status, bool Legal, int Ply,
    IReadOnlyList<string>? Motifs = null);

public sealed partial class ChessEngineService : IChessEngineService
{
    private readonly Func<CancellationToken, Task<ChessLiveGameHost>> _getLiveHost;
    private readonly NpgsqlDataSource? _readOnlyDataSource;
    private ChessLiveGameHost? _liveHost;
    private readonly ILogger _log;

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly ILaplaceEngineRuntime? _injectedRuntime;
    private readonly Func<ILaplaceEngineRuntime?>? _nativeRuntime;
    private readonly object _nativeLock = new();
    private LaplaceEngineRuntime? _ownedRuntime;
    private bool _nativeRuntimeAttempted;
    private NpgsqlDataSource? _ds;
    private ChessModality? _modality;
    private ModalityEngine<ChessState, ChessMove>? _engine;

    private readonly object _statLock = new();
    private CancellationTokenSource? _trainCts;
    private Task? _trainTask;
    private long _games;
    private int _white, _black, _draws, _adjudicated;
    private string _lastOutcome = "";
    private double _trainTemp = 120d, _trainWeight; private int _trainMaxPlies = 400;

    public ChessEngineService(
        double witnessWeight, ChessLiveGameHost liveHost, ILogger? log = null,
        Func<ILaplaceEngineRuntime?>? nativeRuntime = null)
        : this(witnessWeight, liveHost.DataSource, _ => Task.FromResult(liveHost), log, null, nativeRuntime)
    {
        _liveHost = liveHost;
    }

    public ChessEngineService(
        double witnessWeight,
        Func<CancellationToken, Task<ChessLiveGameHost>> getLiveHost, ILogger? log = null)
        : this(witnessWeight, getLiveHost, log, (ILaplaceEngineRuntime?)null)
    {
    }

    public ChessEngineService(
        double witnessWeight,
        Func<CancellationToken, Task<ChessLiveGameHost>> getLiveHost,
        Func<ILaplaceEngineRuntime?> nativeRuntime,
        ILogger? log = null)
        : this(witnessWeight, getLiveHost, log, engineRuntime: null)
    {
        _nativeRuntime = nativeRuntime ?? throw new ArgumentNullException(nameof(nativeRuntime));
    }

    internal ChessEngineService(
        double witnessWeight,
        Func<CancellationToken, Task<ChessLiveGameHost>> getLiveHost,
        ILogger? log,
        ILaplaceEngineRuntime? engineRuntime)
    {
        _getLiveHost = getLiveHost ?? throw new ArgumentNullException(nameof(getLiveHost));
        _trainWeight = witnessWeight;
        _injectedRuntime = engineRuntime;
        _log = log ?? Laplace.Ops.LaplaceLogging.CreateLogger(typeof(ChessEngineService).FullName!);
    }

    /// <summary>
    /// Chess reads run on an existing server-enforced read-only serving datasource; the live
    /// host factory is used only by operations that record or train.
    /// </summary>
    public ChessEngineService(
        double witnessWeight,
        NpgsqlDataSource readOnlyDataSource,
        Func<CancellationToken, Task<ChessLiveGameHost>> getLiveHost,
        ILogger? log = null,
        ILaplaceEngineRuntime? engineRuntime = null,
        Func<ILaplaceEngineRuntime?>? nativeRuntime = null)
    {
        _readOnlyDataSource = readOnlyDataSource ?? throw new ArgumentNullException(nameof(readOnlyDataSource));
        _getLiveHost = getLiveHost ?? throw new ArgumentNullException(nameof(getLiveHost));
        _trainWeight = witnessWeight;
        _injectedRuntime = engineRuntime;
        _nativeRuntime = nativeRuntime;
        _log = log ?? Laplace.Ops.LaplaceLogging.CreateLogger(typeof(ChessEngineService).FullName!);
    }

    /// <summary>Connection policy used by runtime hosts and standalone chess readers.</summary>
    public static string ResolveConnString()
        => LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest);

    public string NewGameFen() => ChessModality.StartFen;

    private async Task<ChessLiveGameHost> LiveHostAsync(CancellationToken ct)
        => _liveHost ??= await _getLiveHost(ct).ConfigureAwait(false);

    private async Task<ModalityEngine<ChessState, ChessMove>> EngineAsync(CancellationToken ct)
    {
        if (_engine is not null) return _engine;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is not null) return _engine;

            NpgsqlDataSource ds;
            if (_readOnlyDataSource is not null)
            {
                ds = _readOnlyDataSource;
            }
            else
            {
                var liveHost = await LiveHostAsync(ct).ConfigureAwait(false);
                ds = liveHost.DataSource;
            }

            var ratings = new ChessNativeRatings(ds);
            var modality = _modality ??= new ChessModality();
            // Every move is the Engine's [position, move, position] claim; no relation type names it.
            var engine = new ModalityEngine<ChessState, ChessMove>(
                modality, Hash128.Zero, ratings, ratings, ChessNativeRatings.Claim);
            _ds = ds;
            _engine = engine;
            _log.LogInformation("chess inference initialized on the Native substrate's recorded standings");
            return engine;
        }
        finally { _initGate.Release(); }
    }

    public Task<ChessLegalResponse> LegalAsync(string fen, CancellationToken ct = default)
        => LegalAsync(fen, variant: null, ct);

    public async Task<ChessLegalResponse> LegalAsync(string fen, string? variant, CancellationToken ct = default)
    {
        var engine = await EngineAsync(ct);
        var state = ParseValidatedState(fen, ChessVariants.Resolve(variant), nameof(fen));
        var terminal = _modality!.Terminal(state);
        var status = terminal is { } t ? Describe(t) : "ongoing";
        var inCheck = MoveGen.InCheck(state.Board, state.Board.WhiteToMove);
        var cands = await engine.ScoreMovesAsync(state, ct);
        var moves = cands
            .OrderByDescending(c => c.EffMu)
            .Select(c => new ChessMoveScore(c.Action.ToUci(), c.EffMu / 1e9, c.Rated))
            .ToList();
        return new ChessLegalResponse(moves, inCheck, status);
    }

    public async Task<IReadOnlyList<ChessMoveScore>> ScoreAsync(string fen, CancellationToken ct = default)
    {
        return (await LegalAsync(fen, ct)).Moves;
    }

    /// <summary>
    /// The recorded games' moves from a position: each legal move's [position, move, position] claim and its
    /// Native standing, the most played first.
    /// </summary>
    public Task<ChessExploreResponse> ExploreAsync(
        string fen, int limit = 12, CancellationToken ct = default)
        => ExploreAsync(fen, limit, variant: null, ct);

    public async Task<ChessExploreResponse> ExploreAsync(
        string fen, int limit, string? variant, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var state = ParseValidatedState(fen, ChessVariants.Resolve(variant), nameof(fen));
        var legal = _modality!.LegalActions(state);
        var claims = new Hash128[legal.Count];
        for (int i = 0; i < legal.Count; i++)
            claims[i] = ChessNativeRatings.Claim(state, legal[i], _modality.Apply(state, legal[i]));
        var standings = await NpgsqlChessTransitionStandings.ReadAsync(
            _ds!, claims.Where(static id => id != Hash128.Zero).Distinct().ToArray(), ct);

        var rows = new List<ChessExploreMove>(legal.Count);
        for (int i = 0; i < legal.Count; i++)
        {
            if (!standings.TryGetValue(claims[i], out var standing)) continue;
            rows.Add(new ChessExploreMove(
                legal[i].ToUci(), San.ToSan(state.Board, legal[i]),
                standing.Confidence - GlickoPriors.NeutralMu / 1e9, standing.Deviation, standing.Matches));
        }
        var ranked = rows
            .OrderByDescending(static r => r.Witnesses)
            .ThenByDescending(static r => r.EffMu)
            .Take(Math.Max(1, limit))
            .ToList();
        return new ChessExploreResponse(fen, ranked);
    }

    public async Task<ChessBestMove> BestMoveAsync(string fen, double temperature = 0d, CancellationToken ct = default)
    {
        _ = temperature;
        return await BestMoveSearchAsync(fen, depth: 4, substrate: true, moves: null, ct)
            .ConfigureAwait(false);
    }

    private SubstrateRootBias? _rootBias;

    private readonly System.Collections.Concurrent.ConcurrentBag<Search> _searchPool = new();

    private Search BuildEngine(bool substrate, int ttBits = 20, SearchContinuationTable? continuations = null)
    {
        IRootBias? bias = substrate ? (_rootBias ??= new SubstrateRootBias(_ds!)) : null;
        if (_searchPool.TryTake(out var pooled) && pooled.TtBits == ttBits)
        {
            pooled.Reconfigure(bias, null, null, null, ChessTablebaseRuntime.ProbeSearch, continuations);
            return pooled;
        }
        return new Search(
            EvalTerm.All, bias, ttBits,
            tablebase: ChessTablebaseRuntime.ProbeSearch,
            continuations: continuations);
    }

    private void ReturnEngine(Search search) => _searchPool.Add(search);

    public Task<ChessPositionEval> EvalPositionAsync(
        string fen, int depth = 4, bool substrate = true, CancellationToken ct = default)
        => EvalPositionAsync(fen, depth, substrate, variant: null, ct);

    public async Task<ChessPositionEval> EvalPositionAsync(
        string fen, int depth, bool substrate, string? variant, CancellationToken ct = default)
    {
        if (substrate)
            await EngineAsync(ct);
        var rules = ChessVariants.Resolve(variant);
        var state = ParseValidatedState(fen, rules, nameof(fen));
        if (_modality!.Terminal(state) is { } term)
        {
            int whiteCp = (term.IsDraw || term.IsUnknown) ? 0 : term.Winner == 0 ? 30_000 : -30_000;
            return new ChessPositionEval(whiteCp, 0, 0, substrate, true, Describe(term), Source: "terminal");
        }

        int boundedDepth = Math.Clamp(depth, 1, 12);
        if (substrate && TryNativeChessPlay(state.Board, rules, boundedDepth) is { StatusCode: 0 } native)
        {
            int nativeWhiteCp = state.Board.WhiteToMove ? native.ScoreCp : -native.ScoreCp;
            return new ChessPositionEval(
                nativeWhiteCp, native.DepthReached, (long)native.NodesSearched, Substrate: true, Terminal: false, "ongoing",
                SyzygyWdl: native.SyzygyWdl, FromSyzygy: native.FromSyzygy, WallUs: (long)native.WallUs, Source: native.Source);
        }

        var search = BuildEngine(substrate, continuations: ContinuationsFor(fen, moves: null, substrate));
        var result = search.Think(state, new Search.Limits(MaxDepth: boundedDepth));
        ReturnEngine(search);
        int score = state.Board.WhiteToMove ? result.Score : -result.Score;
        return new ChessPositionEval(score, result.Depth, result.Nodes, substrate, false, "ongoing", Source: "managed");
    }

    public Task<ChessBestMove> BestMoveSearchAsync(
        string fen, int depth = 4, bool substrate = true, IReadOnlyList<string>? moves = null,
        CancellationToken ct = default)
        => BestMoveSearchAsync(fen, depth, substrate, moves, variant: null, ct);

    public async Task<ChessBestMove> BestMoveSearchAsync(
        string fen, int depth, bool substrate, IReadOnlyList<string>? moves, string? variant,
        CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var rules = ChessVariants.Resolve(variant);
        var state = StateFromHistory(fen, moves, rules);
        if (_modality!.Terminal(state) is { } term)
            return new ChessBestMove(null, state.Board.ToFen(), 0, false, true, Describe(term), Source: "terminal");

        int boundedDepth = Math.Clamp(depth, 1, 12);
        bool reads = substrate && !HasImmediateMate(_modality, state);
        if (reads && TryNativeChessPlay(state.Board, rules, boundedDepth) is { StatusCode: 0 } native
            && ResolveNativeMove(state.Board, native.BestUci) is { } nativeMove)
        {
            var nextNative = _modality.Apply(state, nativeMove);
            var nativeStatus = _modality.Terminal(nextNative) is { } tNative ? Describe(tNative) : "ongoing";
            var nativeMotifs = ChessMotifs.DetectAtPly(state.Board, nativeMove, nextNative.Board).ToList();
            int nativeWhiteCp = state.Board.WhiteToMove ? native.ScoreCp : -native.ScoreCp;
            return new ChessBestMove(
                nativeMove.ToUci(), nextNative.Board.ToFen(), native.ScoreCp, Rated: true,
                nativeStatus != "ongoing", nativeStatus, ScoreCp: nativeWhiteCp,
                Depth: native.DepthReached, Nodes: (long)native.NodesSearched,
                Pv: [nativeMove.ToUci()], Motifs: nativeMotifs,
                SyzygyWdl: native.SyzygyWdl, FromSyzygy: native.FromSyzygy,
                WallUs: (long)native.WallUs, Source: native.Source, BestSan: native.BestSan);
        }

        var search = BuildEngine(reads, continuations: ContinuationsFor(fen, moves, reads, movesIgnoreFen: true));
        var result = search.Think(state, new Search.Limits(MaxDepth: boundedDepth));
        ChessMove mv = result.BestMove!.Value;

        var next = _modality.Apply(state, mv);
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        var pv = search.ExtractPv(state);
        ReturnEngine(search);
        var motifs = ChessMotifs.DetectAtPly(state.Board, mv, next.Board).ToList();
        int whiteCp = state.Board.WhiteToMove ? result.Score : -result.Score;

        return new ChessBestMove(mv.ToUci(), next.Board.ToFen(), result.Score, reads,
            status != "ongoing", status, ScoreCp: whiteCp, Depth: result.Depth, Nodes: result.Nodes,
            Pv: pv, Motifs: motifs, Source: "managed", BestSan: San.ToSan(state.Board, mv));
    }

    internal EngineChessResponse? TryNativeChessPlay(
        Board board, ChessVariantRules rules, int maxDepth,
        int quiesceDepth = 4, ulong maxNodes = 200_000, ulong maxWallUs = 250_000)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("LAPLACE_CHESS_NATIVE"), "0", StringComparison.Ordinal))
            return null;

        var runtime = ResolveNativeRuntime();
        if (runtime is null) return null;

        try
        {
            string? variant = rules.IsStandard ? null : rules.Law;
            return runtime.ChessPlay(new EngineChessRequest(
                Fen: board.ToFen(),
                Variant: variant,
                MaxDepth: maxDepth,
                QuiesceDepth: quiesceDepth,
                MaxNodes: maxNodes,
                MaxWallUs: maxWallUs));
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or ArgumentException)
        {
            _log.LogDebug(ex, "Native lp_engine_chess_play unavailable; falling back to managed Search.");
            return null;
        }
    }

    private ILaplaceEngineRuntime? ResolveNativeRuntime()
    {
        if (_nativeRuntime is not null && _nativeRuntime() is { } supplied) return supplied;
        if (_injectedRuntime is not null) return _injectedRuntime;
        if (_ownedRuntime is not null) return _ownedRuntime;
        lock (_nativeLock)
        {
            if (_ownedRuntime is not null) return _ownedRuntime;
            if (_nativeRuntimeAttempted || _ds is null) return null;
            _nativeRuntimeAttempted = true;
            try
            {
                var conn = _ds.ConnectionString;
                if (string.IsNullOrWhiteSpace(conn)) return null;
                _ownedRuntime = new LaplaceEngineRuntime(ChessGameRecorder.ToLibpqConnInfo(conn));
                return _ownedRuntime;
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or TypeInitializationException)
            {
                _log.LogDebug(ex, "LaplaceEngineRuntime initialization skipped; chess will use managed Search.");
                return null;
            }
        }
    }

    internal static ChessMove? ResolveNativeMove(Board board, string? uci)
    {
        if (string.IsNullOrWhiteSpace(uci)) return null;
        string trimmed = uci.Trim();
        foreach (var m in MoveGen.Legal(board))
        {
            if (string.Equals(m.ToUci(), trimmed, StringComparison.OrdinalIgnoreCase))
                return m;
            if (m.IsCastle && trimmed.Length == 4)
            {
                int rookSq = Board.Sq(board.CastleRookFile(board.WhiteToMove, m.IsKingSideCastle), Board.RankOf(m.From));
                string c960Uci = Board.SquareToAlgebraic(m.From) + Board.SquareToAlgebraic(rookSq);
                if (string.Equals(c960Uci, trimmed, StringComparison.OrdinalIgnoreCase))
                    return m;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether the side to move has a legal move that ends the game in its favour. No
    /// substrate evidence can outrank a mate, so selection skips its substrate reads here
    /// (the root bias); the search still makes the choice
    /// and finds the mate at depth one. Move generation only, no database read.
    /// </summary>
    internal static bool HasImmediateMate(ChessModality modality, ChessState state)
    {
        int mover = state.Board.WhiteToMove ? 0 : 1;
        foreach (var move in MoveGen.Legal(state.Board))
            if (modality.Terminal(modality.Apply(state, move)) is { IsDraw: false, IsUnknown: false } end && end.Winner == mover)
                return true;
        return false;
    }

    private SearchContinuationTable ContinuationsFor(
        string? fen, IReadOnlyList<string>? moves, bool substrate, bool movesIgnoreFen = false)
    {
        _ = substrate;
        string? origin = movesIgnoreFen && moves is { Count: > 0 } ? null : fen;
        return SearchContinuationSeed.ForUci(_modality!, origin, moves);
    }

    private ChessState ParseValidatedState(string fen, ChessVariantRules rules, string paramName = "fen")
    {
        var modality = _modality ??= new ChessModality();
        if (string.IsNullOrWhiteSpace(fen))
            throw new ArgumentException("Chess FEN cannot be empty.", paramName);

        try
        {
            string trimmed = fen.Trim();
            ChessState state = string.Equals(trimmed, "startpos", StringComparison.OrdinalIgnoreCase)
                ? (rules.IsStandard
                    ? modality.Initial(rules)
                    : modality.FromFen(NativeChessRules.LawStart(rules.Law) ?? ChessModality.StartFen, rules))
                : modality.FromFen(trimmed, rules);

            if (rules.IsStandard &&
                (System.Numerics.BitOperations.PopCount(state.Board.PieceBB(Piece.WKing)) != 1 ||
                 System.Numerics.BitOperations.PopCount(state.Board.PieceBB(Piece.BKing)) != 1))
            {
                throw new ArgumentException(
                    $"Invalid chess FEN '{fen}': standard chess requires exactly one white king and one black king.",
                    paramName);
            }

            return state;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"Invalid chess FEN '{fen}': {ex.Message}", paramName, ex);
        }
    }

    private ChessState StateFromHistory(string fen, IReadOnlyList<string>? moves, ChessVariantRules? rules = null)
    {
        var resolved = rules ?? ChessVariantRules.Standard;
        var state = ParseValidatedState(fen, resolved, nameof(fen));
        if (moves is not { Count: > 0 })
            return state;

        for (int i = 0; i < moves.Count; i++)
        {
            string uci = moves[i] ?? "";
            if (_modality!.Terminal(state) is not null)
                throw new ArgumentException($"Illegal UCI move '{uci}' at index {i}: position is already terminal.", nameof(moves));
            if (FindLegalUci(state, uci) is not { } mv)
                throw new ArgumentException($"Illegal UCI move '{uci}' at index {i}.", nameof(moves));
            state = _modality.Apply(state, mv);
        }
        return state;
    }
}
