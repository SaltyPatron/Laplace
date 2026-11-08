using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
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
    IReadOnlyList<string>? Pv = null, IReadOnlyList<string>? Motifs = null);

public sealed record ChessPositionEval(
    int WhiteScoreCp, int Depth, long Nodes, bool Substrate, bool Terminal, string Status);

public sealed record ChessApplyResult(
    string Fen, bool Terminal, string Status, bool Legal, IReadOnlyList<string>? Motifs = null);

public sealed record ChessTrainStatus(
    bool Running, long Games, int White, int Black, int Draws, int Adjudicated,
    string LastOutcome, double Temperature, double Weight, int MaxPlies);

public sealed record ChessExploreMove(
    string Uci, string San, double EffMu, double Rd, long Witnesses,
    long PlayerGames, double? PlayerScore);

public sealed record ChessExploreResponse(
    string Fen, string? Player, IReadOnlyList<ChessExploreMove> Moves);

public sealed record ChessPlayStart(Guid SessionId, string Fen, string Status = "ongoing", int Ply = 0,
    string TenantId = "public", string? UserId = null, string GameId = "", OperationPrincipal? Requester = null);

public sealed record ChessPlayMoveResult(
    string Fen, bool Terminal, string Status, bool Legal, int Ply,
    IReadOnlyList<string>? Motifs = null);

public sealed partial class ChessEngineService : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<ChessLiveGameHost>> _getLiveHost;
    private readonly NpgsqlDataSource? _readOnlyDataSource;
    private ChessLiveGameHost? _liveHost;
    private readonly ILogger _log;

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _writeInitGate = new(1, 1);
    private NpgsqlDataSource? _ds;
    private SubstrateTurnHost? _host;
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
        double witnessWeight, ChessLiveGameHost liveHost, ILogger? log = null)
        : this(witnessWeight, liveHost.DataSource, _ => Task.FromResult(liveHost), log)
    {
        _liveHost = liveHost;
    }

    public ChessEngineService(
        double witnessWeight,
        Func<CancellationToken, Task<ChessLiveGameHost>> getLiveHost, ILogger? log = null)
    {
        _getLiveHost = getLiveHost ?? throw new ArgumentNullException(nameof(getLiveHost));
        _trainWeight = witnessWeight;
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
        ILogger? log = null)
    {
        _readOnlyDataSource = readOnlyDataSource ?? throw new ArgumentNullException(nameof(readOnlyDataSource));
        _getLiveHost = getLiveHost ?? throw new ArgumentNullException(nameof(getLiveHost));
        _trainWeight = witnessWeight;
        _log = log ?? Laplace.Ops.LaplaceLogging.CreateLogger(typeof(ChessEngineService).FullName!);
    }

    /// <summary>Connection policy used by runtime hosts and standalone chess readers.</summary>
    public static string ResolveConnString()
        => LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest);

    public string NewGameFen() => ChessModality.StartFen;

    private async Task<ChessLiveGameHost> LiveHostAsync(CancellationToken ct)
        => _liveHost ??= await _getLiveHost(ct).ConfigureAwait(false);

    private async Task<SubstrateTurnHost> WriteHostAsync(CancellationToken ct)
    {
        if (_host is not null) return _host;
        await _writeInitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host is not null) return _host;
            var liveHost = await LiveHostAsync(ct).ConfigureAwait(false);
            _host = new SubstrateTurnHost(
                liveHost.DataSource, liveHost.Writer,
                new NpgsqlSubstrateReader(liveHost.DataSource), _trainWeight);
            return _host;
        }
        finally { _writeInitGate.Release(); }
    }

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

            var host = new SubstrateTurnReadHost(ds);
            LoadPerfcache();
            var modality = _modality ??= new ChessModality();
            var engine = new ModalityEngine<ChessState, ChessMove>(
                modality, ChessVocabulary.MoveType, host, host);
            _ds = ds;
            _engine = engine;
            ScheduleLearnedRefresh();
            _log.LogInformation("chess inference initialized on read-only substrate host");
            return engine;
        }
        finally { _initGate.Release(); }
    }

    private static void LoadPerfcache() => CodepointPerfcache.LoadDefault();

    public async Task<ChessLegalResponse> LegalAsync(string fen, CancellationToken ct = default)
    {
        var engine = await EngineAsync(ct);
        var state = _modality!.FromFen(fen);
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

    public async Task<ChessExploreResponse> ExploreAsync(
        string fen, string? player = null, int limit = 12, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var state = _modality!.FromFen(fen);
        var legal = _modality.LegalActions(state);

        var byChild = new Dictionary<Hash128, (string Uci, string San)>(legal.Count);
        Hash128 rootId;
        lock (ChessCompose.Gate)
        {
            rootId = ChessCompose.PositionId(state.Board);
            foreach (var mv in legal)
            {
                var next = _modality.Apply(state, mv);
                var childId = ChessCompose.PositionId(next.Board);
                byChild[childId] = (mv.ToUci(), San.ToSan(state.Board, mv));
            }
        }

        int candidateLimit = Math.Max(limit, legal.Count);
        var materializedTask = NpgsqlSubstrateReads.ChessMovesAsync(
            _ds!, rootId.ToBytes(), candidateLimit, ct);
        Task<IReadOnlyList<NpgsqlSubstrateReads.ChessPlayerMoveRow>>? playerTask = null;
        if (!string.IsNullOrWhiteSpace(player))
            playerTask = NpgsqlSubstrateReads.ChessPlayerMovesAsync(
                _ds!, rootId.ToBytes(), ChessVocabulary.PlayerId(player).ToBytes(),
                state.Board.WhiteToMove, candidateLimit, ct);

        var materializedRows = await materializedTask;
        var rows = new List<ChessExploreMove>(candidateLimit);
        var playerStats = new Dictionary<Hash128, (long Games, double Score)>();

        if (playerTask is not null)
        {
            var playerRows = await playerTask;
            foreach (var pr in playerRows)
                playerStats[Hash128.FromBytes(pr.NextPosition)] = (pr.Games, pr.Score);
        }

        foreach (var mr in materializedRows)
        {
            var childId = Hash128.FromBytes(mr.NextPosition);
            if (!byChild.TryGetValue(childId, out var mv)) continue;
            var ps = playerStats.TryGetValue(childId, out var s) ? s : (Games: 0L, Score: 0d);
            rows.Add(new ChessExploreMove(
                mv.Uci, mv.San, mr.EffMu - GlickoPriors.NeutralMu / 1e9,
                mr.Rd, mr.WitnessCount, ps.Games, ps.Games > 0 ? ps.Score : null));
        }

        var seen = new HashSet<string>(rows.Select(x => x.Uci));
        foreach (var (childId, s) in playerStats)
        {
            if (!byChild.TryGetValue(childId, out var known) || !seen.Add(known.Uci)) continue;
            rows.Add(new ChessExploreMove(known.Uci, known.San, 0d, 0d, 0, s.Games, s.Score));
        }

        var ranked = rows
            .OrderByDescending(r => playerTask is null ? r.Witnesses : r.PlayerGames)
            .ThenByDescending(r => r.EffMu)
            .Take(limit)
            .ToList();
        return new ChessExploreResponse(fen, player, ranked);
    }

    public async Task<ChessBestMove> BestMoveAsync(string fen, double temperature = 0d, CancellationToken ct = default)
    {
        _ = temperature;
        return await BestMoveSearchAsync(fen, depth: 4, substrate: true, moves: null, ct)
            .ConfigureAwait(false);
    }

    private SubstrateRootBias? _rootBias;
    private SubstrateBoardEvaluator? _boardEvaluator;
    private bool _learnedTried;
    private int[][]? _lpMg, _lpEg;
    private Task? _learnedRefresh;
    private readonly object _learnedGate = new();

    private (int[][]? Mg, int[][]? Eg) LearnedPstBlend(bool refresh = false)
    {
        if (refresh)
        {
            lock (_learnedGate) { _learnedTried = false; _learnedCells = null; }
        }
        if (!_learnedTried) ScheduleLearnedRefresh();
        return (_lpMg, _lpEg);
    }

    private void ScheduleLearnedRefresh()
    {
        lock (_learnedGate)
        {
            if (_learnedTried || _learnedRefresh is { IsCompleted: false }) return;
            _learnedTried = true;
            _learnedRefresh = Task.Run(() =>
            {
                try
                {
                    var cells = LearnedPst.ReadWhite(_ds!);
                    var (lm, le) = LearnedPst.BuildTables(_ds!);
                    var blended = Evaluation.BlendPeStoWith(lm, le);
                    lock (_learnedGate)
                    {
                        _learnedCells = cells;
                        (_lpMg, _lpEg) = blended;
                    }
                }
                catch (Exception error)
                {
                    lock (_learnedGate) { _lpMg = null; _lpEg = null; }
                    _log.LogWarning(error, "Learned piece-square refresh failed; learned tables are unavailable.");
                }
            });
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentBag<Search> _searchPool = new();

    private Search BuildEngine(bool substrate, int ttBits = 20, SearchContinuationTable? continuations = null)
    {
        IRootBias? bias = substrate ? (_rootBias ??= new SubstrateRootBias(_ds!)) : null;
        ISearchPositionEvaluator? evaluator = substrate
            ? (_boardEvaluator ??= new SubstrateBoardEvaluator(_ds!)) : null;
        if (_searchPool.TryTake(out var pooled) && pooled.TtBits == ttBits)
        {
            pooled.Reconfigure(
                bias, null, null, evaluator, ChessTablebaseRuntime.ProbeSearch, continuations);
            return pooled;
        }
        return new Search(
            EvalTerm.All, bias, ttBits,
            positionEvaluator: evaluator,
            tablebase: ChessTablebaseRuntime.ProbeSearch,
            continuations: continuations);
    }

    private void ReturnEngine(Search search) => _searchPool.Add(search);

    public async Task<ChessPositionEval> EvalPositionAsync(
        string fen, int depth = 4, bool substrate = true, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var state = _modality!.FromFen(fen);
        if (_modality!.Terminal(state) is { } term)
        {
            int whiteCp = term.IsDraw ? 0 : term.Winner == 0 ? 30_000 : -30_000;
            return new ChessPositionEval(whiteCp, 0, 0, substrate, true, Describe(term));
        }

        var search = BuildEngine(substrate, continuations: ContinuationsFor(fen, moves: null, substrate));
        var result = search.Think(state, new Search.Limits(MaxDepth: Math.Clamp(depth, 1, 12)));
        ReturnEngine(search);
        int score = state.Board.WhiteToMove ? result.Score : -result.Score;
        return new ChessPositionEval(score, result.Depth, result.Nodes, substrate, false, "ongoing");
    }

    public async Task<ChessBestMove> BestMoveSearchAsync(
        string fen, int depth = 4, bool substrate = true, IReadOnlyList<string>? moves = null,
        CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var state = StateFromHistory(fen, moves);
        if (_modality!.Terminal(state) is { } term)
            return new ChessBestMove(null, state.Board.ToFen(), 0, false, true, Describe(term));

        var search = BuildEngine(substrate, continuations: ContinuationsFor(fen, moves, substrate, movesIgnoreFen: true));
        var result = search.Think(state, new Search.Limits(MaxDepth: Math.Clamp(depth, 1, 12)));
        ChessMove mv = result.BestMove!.Value;

        var next = _modality.Apply(state, mv);
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        var pv = search.ExtractPv(state);
        ReturnEngine(search);
        var motifs = ChessMotifs.DetectAtPly(state.Board, mv, next.Board).ToList();
        int whiteCp = state.Board.WhiteToMove ? result.Score : -result.Score;

        return new ChessBestMove(mv.ToUci(), next.Board.ToFen(), result.Score, substrate,
            status != "ongoing", status, ScoreCp: whiteCp, Depth: result.Depth, Nodes: result.Nodes,
            Pv: pv, Motifs: motifs);
    }

    private SearchContinuationTable ContinuationsFor(
        string? fen, IReadOnlyList<string>? moves, bool substrate, bool movesIgnoreFen = false)
    {
        string? origin = movesIgnoreFen && moves is { Count: > 0 } ? null : fen;
        return SearchContinuationSeed.ForUci(
            _modality!, origin, moves, substrate ? _ds : null);
    }

    private ChessState StateFromHistory(string fen, IReadOnlyList<string>? moves)
    {
        if (moves is not { Count: > 0 })
            return _modality!.FromFen(fen);

        var state = _modality!.Initial();
        foreach (var uci in moves)
        {
            if (FindLegalUci(state, uci) is not { } mv) break;
            state = _modality.Apply(state, mv);
        }
        return state;
    }
}
