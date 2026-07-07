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

public sealed class ChessEngineService : IAsyncDisposable
{
    private readonly string _connString;
    private readonly double _witnessWeight;
    private readonly ILogger _log;

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private NpgsqlDataSource? _ds;
    private ConsensusAccumulatingWriter? _writer;
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

    public ChessEngineService(string connString, double witnessWeight = 0.5d, ILogger? log = null)
    {
        _connString = connString;
        _witnessWeight = witnessWeight;
        _trainWeight = witnessWeight;
        _log = log ?? NullLogger.Instance;
    }

    public static string ResolveConnString() => LaplaceInstall.PostgresConnectionString();

    public string NewGameFen() => ChessModality.StartFen;






    private async Task<ModalityEngine<ChessState, ChessMove>> EngineAsync(CancellationToken ct)
    {
        if (_engine is not null) return _engine;
        await _initGate.WaitAsync(ct);
        try
        {
            if (_engine is not null) return _engine;
            LoadPerfcache();
            var ds = new NpgsqlDataSourceBuilder(_connString).Build();
            var inner = new NpgsqlSubstrateWriter(ds);
            var writer = new ConsensusAccumulatingWriter(
                inner, ds, foldWorkers: 1, freshSource: false, persistEvidence: true, stageAsWalks: false);
            var reader = new NpgsqlSubstrateReader(ds);
            var host = new SubstrateTurnHost(ds, writer, reader, _witnessWeight);
            var modality = new ChessModality();
            var engine = new ModalityEngine<ChessState, ChessMove>(modality, ChessVocabulary.MoveType, host, host);
            var canonicalNames = await ChessVocabulary.BootstrapAsync(writer, ct);
            await RegisterCanonicalsAsync(ds, canonicalNames, ct);
            _ds = ds; _writer = writer; _host = host; _modality = modality; _engine = engine;
            _log.LogInformation("chess engine initialized against {Conn}", Redact(_connString));
            return engine;
        }
        finally { _initGate.Release(); }
    }




    private static async Task RegisterCanonicalsAsync(
        NpgsqlDataSource ds, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        if (names.Count == 0) return;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT laplace.register_canonicals(@names)";
        cmd.Parameters.Add(new NpgsqlParameter
        {
            ParameterName = "names",
            Value = names.ToArray(),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text,
        });
        await cmd.ExecuteNonQueryAsync(ct);
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

    public async Task<ChessBestMove> BestMoveAsync(string fen, double temperature = 0d, CancellationToken ct = default)
    {
        var engine = await EngineAsync(ct);
        var state = _modality!.FromFen(fen);
        if (_modality!.Terminal(state) is { } term)
            return new ChessBestMove(null, state.Board.ToFen(), 0, false, true, Describe(term));

        var cands = await engine.ScoreMovesAsync(state, ct);
        var chosen = ModalityEngine<ChessState, ChessMove>.Select(cands, temperature, Rng());
        var next = chosen.Next;
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        return new ChessBestMove(chosen.Action.ToUci(), next.Board.ToFen(), chosen.EffMu / 1e9,
            chosen.Rated, status != "ongoing", status);
    }






    private SubstructureFoldBias? _foldBias;
    private bool _learnedTried;
    private int[][]? _lpMg, _lpEg;

    private SubstructureFoldBias FoldBias() => _foldBias ??= new SubstructureFoldBias(_ds!);

    private (int[][]? Mg, int[][]? Eg) LearnedPstBlend(bool refresh = false)
    {
        if (refresh) _learnedTried = false;
        if (_learnedTried) return (_lpMg, _lpEg);
        _learnedTried = true;
        try { var (lm, le) = LearnedPst.BuildTables(_ds!, 1.0); (_lpMg, _lpEg) = Evaluation.BlendPeStoWith(lm, le); }
        catch { _lpMg = null; _lpEg = null; }
        return (_lpMg, _lpEg);
    }

    private Search BuildEngine(bool substrate, int ttBits = 20)
    {
        var (mg, eg) = LearnedPstBlend();
        return new Search(EvalTerm.All, substrate ? FoldBias() : null, ttBits, mg, eg);
    }

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

        var search = BuildEngine(substrate);
        var result = search.Think(state.Board, new Search.Limits(MaxDepth: Math.Clamp(depth, 1, 12)));
        int score = state.Board.WhiteToMove ? result.Score : -result.Score;
        return new ChessPositionEval(score, result.Depth, result.Nodes, substrate, false, "ongoing");
    }

    public async Task<ChessBestMove> BestMoveSearchAsync(
        string fen, int depth = 4, bool substrate = true, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var state = _modality!.FromFen(fen);
        if (_modality!.Terminal(state) is { } term)
            return new ChessBestMove(null, state.Board.ToFen(), 0, false, true, Describe(term));

        var search = BuildEngine(substrate);
        var result = search.Think(state.Board, new Search.Limits(MaxDepth: Math.Clamp(depth, 1, 12)));
        if (result.BestMove is not { } mv)
            return new ChessBestMove(null, state.Board.ToFen(), 0, false, false, "no legal move");

        var next = _modality.Apply(state, mv);
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        var pv = search.ExtractPv(state.Board);
        var motifs = ChessMotifs.DetectAtPly(state.Board, mv, next.Board).ToList();
        int whiteCp = state.Board.WhiteToMove ? result.Score : -result.Score;

        return new ChessBestMove(mv.ToUci(), next.Board.ToFen(), result.Score, substrate,
            status != "ongoing", status, ScoreCp: whiteCp, Depth: result.Depth, Nodes: result.Nodes,
            Pv: pv, Motifs: motifs);
    }

    public async Task RunStrongSelfPlayAsync(
    int games, int depth, int maxPlies, int openingPlies, int reportEvery,
    Action<ChessTrainStatus>? onReport, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var rng = new Random();
        for (int g = 1; g <= games && !ct.IsCancellationRequested; g++)
        {

            bool refresh = (g - 1) % Math.Max(1, reportEvery) == 0;
            var (mg, eg) = LearnedPstBlend(refresh);
            var search = new Search(EvalTerm.All, FoldBias(), ttBits: 18, mgPst: mg, egPst: eg);

            var state = _modality!.Initial();
            var subjectKeys = new List<string>(); var objectKeys = new List<string>(); var movers = new List<int>();
            int plies = 0; bool adjudicated = false;
            var terminal = _modality.Terminal(state);
            while (terminal is null)
            {
                if (plies >= maxPlies) { adjudicated = true; break; }
                var legal = _modality.LegalActions(state);
                ChessMove mv = plies < openingPlies
                    ? legal[rng.Next(legal.Count)]
                    : search.Think(state.Board, new Search.Limits(MaxDepth: depth)).BestMove ?? legal[rng.Next(legal.Count)];
                int mover = _modality.SideToMove(state);
                var next = _modality.Apply(state, mv);
                subjectKeys.Add(_modality.StateKey(state));
                objectKeys.Add(_modality.StateKey(next));
                movers.Add(mover);
                state = next; plies++;
                terminal = _modality.Terminal(state);
            }

            var outcome = terminal ?? GameOutcome.Draw;
            var edges = new RecordedEdge[subjectKeys.Count];
            for (int i = 0; i < edges.Length; i++)
                edges[i] = new RecordedEdge(subjectKeys[i], objectKeys[i], null, outcome.ForMover(movers[i]));
            await _host!.LearnGameAsync(edges, adjudicated, ct);

            RecordGame(new PlayedGame<ChessMove>(edges, outcome, plies, adjudicated));
            if (onReport is not null && (g % Math.Max(1, reportEvery) == 0 || g == games)) onReport(Status());
        }
    }

    public async Task<ChessApplyResult> ApplyMoveAsync(string fen, string uci, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var state = _modality!.FromFen(fen);
        ChessMove? mv = null;
        foreach (var m in _modality!.LegalActions(state))
            if (m.ToUci() == uci) { mv = m; break; }
        if (mv is null)
            return new ChessApplyResult(fen, false, "illegal move", Legal: false);
        var next = _modality.Apply(state, mv.Value);
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        var motifs = ChessMotifs.DetectAtPly(state.Board, mv.Value, next.Board).ToList();
        return new ChessApplyResult(next.Board.ToFen(), status != "ongoing", status, Legal: true, Motifs: motifs);
    }



    public async Task<IReadOnlyList<LearnedSquare>> LearnedPstAsync(CancellationToken ct = default)
    {
        await EngineAsync(ct);
        return LearnedPst.ReadWhite(_ds!);
    }

    public bool StartTraining(double temperature, double weight, int maxPlies, int maxGames = 0)
    {
        lock (_statLock)
        {
            if (_trainTask is { IsCompleted: false }) return false;
            _trainTemp = temperature; _trainWeight = weight; _trainMaxPlies = maxPlies;
            _trainCts = new CancellationTokenSource();
            _trainTask = Task.Run(() => TrainLoopAsync(maxGames, _trainCts.Token));
            return true;
        }
    }

    public bool StopTraining()
    {
        CancellationTokenSource? cts;
        lock (_statLock) { cts = _trainCts; }
        if (cts is null) return false;
        cts.Cancel();
        return true;
    }

    public ChessTrainStatus Status()
    {
        lock (_statLock)
            return new ChessTrainStatus(
                _trainTask is { IsCompleted: false }, _games, _white, _black, _draws, _adjudicated,
                _lastOutcome, _trainTemp, _trainWeight, _trainMaxPlies);
    }

    public async Task RunSelfPlayAsync(
    int games, double temperature, int maxPlies, int reportEvery,
    Action<ChessTrainStatus>? onReport, CancellationToken ct = default)
    {
        var engine = await EngineAsync(ct);
        var rng = new Random();
        lock (_statLock) { _trainTemp = temperature; _trainMaxPlies = maxPlies; }
        for (int g = 1; g <= games && !ct.IsCancellationRequested; g++)
        {
            var played = await engine.PlayGameAsync(_modality!.Initial(), temperature, rng, maxPlies, ct);
            await _host!.LearnGameAsync(played.Edges, played.Adjudicated, ct);
            RecordGame(played);
            if (onReport is not null && (g % Math.Max(1, reportEvery) == 0 || g == games))
                onReport(Status());
        }
    }

    private async Task TrainLoopAsync(int maxGames, CancellationToken ct)
    {
        var engine = await EngineAsync(ct);
        var rng = new Random();
        int played = 0;
        while (!ct.IsCancellationRequested && (maxGames <= 0 || played < maxGames))
        {
            try
            {
                var g = await engine.PlayGameAsync(_modality!.Initial(), _trainTemp, rng, _trainMaxPlies, ct);
                await _host!.LearnGameAsync(g.Edges, g.Adjudicated, ct);
                RecordGame(g);
                played++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "chess train game failed"); }
        }
    }

    private void RecordGame(PlayedGame<ChessMove> played)
    {
        lock (_statLock)
        {
            _games++;
            if (played.Adjudicated) _adjudicated++;
            if (played.Outcome.IsDraw) { _draws++; _lastOutcome = "draw"; }
            else if (played.Outcome.Winner == 0) { _white++; _lastOutcome = "white"; }
            else { _black++; _lastOutcome = "black"; }
        }
    }

    private Random Rng() => new();

    private static string Describe(GameOutcome o) =>
        o.IsDraw ? "draw" : o.Winner == 0 ? "white wins" : "black wins";

    private static string Redact(string conn) =>
        System.Text.RegularExpressions.Regex.Replace(conn, "(?i)password=[^;]*", "password=***");

    public async ValueTask DisposeAsync()
    {
        StopTraining();
        if (_trainTask is not null) { try { await _trainTask; } catch { } }
        if (_writer is not null) await _writer.DisposeAsync();
        if (_ds is not null) await _ds.DisposeAsync();
        _initGate.Dispose();
    }
}
