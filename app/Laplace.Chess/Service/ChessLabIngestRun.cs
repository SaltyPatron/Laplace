using System.Diagnostics;
using global::Npgsql;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.Extensions.Logging;

namespace Laplace.Chess.Service;

/// <summary>
/// One web-triggered chess import (Lab provider fetch, gauntlet PGN loop-closure) as an
/// ingest run. It drives the same <see cref="IIngestObservability"/> receipts the CLI's
/// IngestRunner writes - <c>laplace.ingest_run_journal</c> for the run and
/// <c>laplace.ingest_file_journal</c> for each admitted unit (a Chess.com monthly archive,
/// a provider PGN, a gauntlet games.pgn) - under the same <c>ChessPgn</c> source and layer as
/// <c>laplace-mono ingest chess</c>. Every exit path reaches exactly one terminal status:
/// ok, failed (with the error text), or cancelled. The journal is operational metadata and
/// never aborts the import; the same lines go to the host log so IIS/journald show them.
/// </summary>
internal sealed class ChessLabIngestRun
{
    internal const string SourceName = ChessPgnDecomposer.PgnSourceName;

    private readonly IIngestObservability _journal;
    private readonly ILogger _log;
    private readonly string _jobId;
    private readonly string _label;
    private readonly Func<Guid> _runId;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private ChessPgnIngestor? _ingestor;
    private long _gamesParsed, _gamesApplied, _gamesFailed;
    private int _unitsDone;
    private bool _finished;

    internal ChessLabIngestRun(
        IIngestObservability journal, ILogger log, string jobId, string label, Func<Guid>? runId = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _jobId = jobId;
        _label = label;
        _runId = runId ?? (static () => Guid.Empty);
    }

    internal Guid RunId => _runId();

    internal bool IsFinished { get { lock (_gate) return _finished; } }

    /// <summary>
    /// Opens the run on the host's ingest datasource. The journal's run-start writes are
    /// synchronous (as in the CLI), so they leave the request thread.
    /// </summary>
    internal static async Task<ChessLabIngestRun> StartAsync(
        NpgsqlDataSource dataSource, ILogger log, string jobId, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ct.ThrowIfCancellationRequested();
        var journal = new NpgsqlIngestObservability(dataSource, logger: log);
        var run = new ChessLabIngestRun(journal, log, jobId, label, () => journal.RunId);
        await Task.Run(run.Start, CancellationToken.None).ConfigureAwait(false);
        return run;
    }

    internal void Start()
    {
        _journal.OnRunStart(SourceName, ChessPgnDecomposer.PgnLayerOrder, inventory: null);
        _log.LogInformation(
            "CHESS_IMPORT_RUN_START job={JobId} run={RunId} source={Source} label={Label}",
            _jobId, RunId, SourceName, _label);
    }

    /// <summary>Row counters are read from the ingestor's acknowledged applies.</summary>
    internal void Attach(ChessPgnIngestor ingestor) => _ingestor = ingestor;

    internal string FileLabel(string unit) => $"{_label}/{unit}";

    /// <summary>
    /// One admitted unit as one file row: running, then ok/failed/cancelled with the error.
    /// A failure is rethrown unchanged; journaling only observes it.
    /// </summary>
    internal async Task<ChessPgnIngestor.Result> IngestUnitAsync(
        string unit, int games, Func<Task<ChessPgnIngestor.Result>> ingest)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        string file = FileLabel(unit);
        _journal.OnFileStarted(SourceName, file);
        ChessPgnIngestor.Result result;
        try
        {
            result = await ingest().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bool cancelled = ex is OperationCanceledException;
            Interlocked.Add(ref _gamesFailed, games);
            _journal.OnFileFinished(SourceName, file, cancelled ? "cancelled" : "failed", Describe(ex));
            if (cancelled)
                _log.LogInformation("CHESS_IMPORT_UNIT job={JobId} run={RunId} unit={Unit} status=cancelled",
                    _jobId, RunId, file);
            else
                _log.LogWarning("CHESS_IMPORT_UNIT job={JobId} run={RunId} unit={Unit} status=failed games={Games} error={Error}",
                    _jobId, RunId, file, games, Describe(ex));
            throw;
        }
        Interlocked.Add(ref _gamesParsed, result.Parsed);
        Interlocked.Add(ref _gamesApplied, result.Applied);
        Interlocked.Increment(ref _unitsDone);
        _journal.OnFileProgress(SourceName, file, result.Parsed, 0, 0, 0);
        _journal.OnFileFinished(SourceName, file, "ok");
        _log.LogInformation(
            "CHESS_IMPORT_UNIT job={JobId} run={RunId} unit={Unit} status=ok games={Games} parsed={Parsed} "
            + "novel={Novel} applied={Applied} repaired={Repaired} verified={Verified}",
            _jobId, RunId, file, games, result.Parsed, result.Novel, result.Applied, result.Repaired, result.Verified);
        return result;
    }

    internal void Complete() => Finish("ok", null);

    internal void Fail(Exception error) => Finish(
        error is OperationCanceledException ? "cancelled" : "failed", Describe(error));

    internal void Cancel() => Finish("cancelled", "cancelled");

    private void Finish(string status, string? error)
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
        }
        var inserted = _ingestor?.Inserted ?? default;
        long parsed = Interlocked.Read(ref _gamesParsed);
        long failed = Interlocked.Read(ref _gamesFailed);
        var result = new IngestRunResult(
            ChessVocabulary.PgnSourceId, SourceName,
            UnitsAttempted: parsed + failed,
            UnitsApplied: Interlocked.Read(ref _gamesApplied),
            UnitsFailed: failed,
            EntitiesInserted: inserted.Entities,
            PhysicalitiesInserted: inserted.Physicalities,
            AttestationsInserted: inserted.Attestations,
            TotalRoundTrips: 0,
            WallClock: _clock.Elapsed,
            Failures: [],
            FilesDone: Volatile.Read(ref _unitsDone),
            InputUnitsDone: parsed,
            InputUnitsTotal: parsed + failed);
        _journal.OnRunFinished(SourceName, result, status, error);
        int retries = _ingestor?.ConcurrentLandingRetries ?? 0;
        if (status == "failed")
            _log.LogWarning(
                "CHESS_IMPORT_RUN_END job={JobId} run={RunId} source={Source} status={Status} games={Games} "
                + "applied={Applied} failed_games={Failed} units={Units} rows_new={Ent}e+{Phys}p+{Att}a "
                + "concurrent_landing_retries={Retries} elapsed_ms={Ms} error={Error}",
                _jobId, RunId, SourceName, status, parsed, result.UnitsApplied, failed, result.FilesDone,
                inserted.Entities, inserted.Physicalities, inserted.Attestations, retries,
                (long)_clock.Elapsed.TotalMilliseconds, error);
        else
            _log.LogInformation(
                "CHESS_IMPORT_RUN_END job={JobId} run={RunId} source={Source} status={Status} games={Games} "
                + "applied={Applied} failed_games={Failed} units={Units} rows_new={Ent}e+{Phys}p+{Att}a "
                + "concurrent_landing_retries={Retries} elapsed_ms={Ms}",
                _jobId, RunId, SourceName, status, parsed, result.UnitsApplied, failed, result.FilesDone,
                inserted.Entities, inserted.Physicalities, inserted.Attestations, retries,
                (long)_clock.Elapsed.TotalMilliseconds);
    }

    internal static string Describe(Exception error)
    {
        var root = error;
        while (root is InvalidDataException { InnerException: { } inner }) root = inner;
        string text = $"[{error.GetType().Name}] {error.Message}";
        if (!ReferenceEquals(root, error) && root is PostgresException pg)
            text += $" ({pg.SqlState}: {pg.MessageText})";
        return text;
    }
}
