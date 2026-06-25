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

public sealed record ChessBestMove(string? Uci, string Fen, double EffMu, bool Rated, bool Terminal, string Status);

public sealed record ChessApplyResult(string Fen, bool Terminal, string Status, bool Legal);

public sealed record ChessTrainStatus(
    bool Running, long Games, int White, int Black, int Draws, int Adjudicated,
    string LastOutcome, double Temperature, double Weight, int MaxPlies);

/// <summary>
/// The chess bot as a long-lived service: plays moves on demand and runs self-play training in the
/// background, all over the live substrate (online incremental fold per game). Shared by the CLI and
/// the web host. The substrate database is <c>LAPLACE_CHESS_DB</c> (falls back to <c>LAPLACE_DB</c>),
/// so chess training need not pollute the main substrate.
/// </summary>
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

    public static string ResolveConnString() =>
        Environment.GetEnvironmentVariable("LAPLACE_CHESS_DB")
        ?? Environment.GetEnvironmentVariable("LAPLACE_DB")
        ?? "Host=localhost;Username=postgres;Password=postgres;Database=laplace_chess_demo";

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
            var inner = new NpgsqlSubstrateWriter(ds, bulkFreshSource: false);
            var writer = new ConsensusAccumulatingWriter(
                inner, ds, foldWorkers: 1, freshSource: false, persistEvidence: true, stageAsWalks: false);
            var reader = new NpgsqlSubstrateReader(ds);
            var host = new SubstrateTurnHost(ds, writer, reader, _witnessWeight);
            var modality = new ChessModality();
            var engine = new ModalityEngine<ChessState, ChessMove>(modality, ChessVocabulary.MoveType, host, host);
            await ChessVocabulary.BootstrapAsync(writer, ct);
            _ds = ds; _writer = writer; _host = host; _modality = modality; _engine = engine;
            _log.LogInformation("chess engine initialized against {Conn}", Redact(_connString));
            return engine;
        }
        finally { _initGate.Release(); }
    }

    private static void LoadPerfcache()
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) { CodepointPerfcache.Load(env); return; }
        CodepointPerfcache.LoadDefault();
    }

    public async Task<IReadOnlyList<ChessMoveScore>> ScoreAsync(string fen, CancellationToken ct = default)
    {
        var engine = await EngineAsync(ct);
        var state = _modality!.FromFen(fen);
        var cands = await engine.ScoreMovesAsync(state, ct);
        return cands
            .OrderByDescending(c => c.EffMu)
            .Select(c => new ChessMoveScore(c.Action.ToUci(), c.EffMu / 1e9, c.Rated))
            .ToList();
    }

    public async Task<ChessBestMove> BestMoveAsync(string fen, double temperature = 0d, CancellationToken ct = default)
    {
        var engine = await EngineAsync(ct);
        var state = _modality!.FromFen(fen);
        if (_modality.Terminal(state) is { } term)
            return new ChessBestMove(null, state.Board.ToFen(), 0, false, true, Describe(term));

        var cands = await engine.ScoreMovesAsync(state, ct);
        var chosen = ModalityEngine<ChessState, ChessMove>.Select(cands, temperature, Rng());
        var next = chosen.Next;
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        return new ChessBestMove(chosen.Action.ToUci(), next.Board.ToFen(), chosen.EffMu / 1e9,
            chosen.Rated, status != "ongoing", status);
    }

    public async Task<ChessApplyResult> ApplyMoveAsync(string fen, string uci, CancellationToken ct = default)
    {
        await EngineAsync(ct);
        var state = _modality!.FromFen(fen);
        ChessMove? mv = null;
        foreach (var m in _modality.LegalActions(state))
            if (m.ToUci() == uci) { mv = m; break; }
        if (mv is null)
            return new ChessApplyResult(fen, false, "illegal move", Legal: false);
        var next = _modality.Apply(state, mv.Value);
        var status = _modality.Terminal(next) is { } t ? Describe(t) : "ongoing";
        return new ChessApplyResult(next.Board.ToFen(), status != "ongoing", status, Legal: true);
    }

    // ---- training ----

    public bool StartTraining(double temperature, double weight, int maxPlies)
    {
        lock (_statLock)
        {
            if (_trainTask is { IsCompleted: false }) return false;
            _trainTemp = temperature; _trainWeight = weight; _trainMaxPlies = maxPlies;
            _trainCts = new CancellationTokenSource();
            _trainTask = Task.Run(() => TrainLoopAsync(_trainCts.Token));
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

    /// <summary>Bounded self-play (CLI): play N games, learn each online, report every R games.</summary>
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
            await _host!.LearnGameAsync(played.Edges, ct);
            RecordGame(played);
            if (onReport is not null && (g % Math.Max(1, reportEvery) == 0 || g == games))
                onReport(Status());
        }
    }

    private async Task TrainLoopAsync(CancellationToken ct)
    {
        var engine = await EngineAsync(ct);
        var rng = new Random();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var played = await engine.PlayGameAsync(_modality!.Initial(), _trainTemp, rng, _trainMaxPlies, ct);
                await _host!.LearnGameAsync(played.Edges, ct);
                RecordGame(played);
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
