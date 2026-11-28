using System.Diagnostics;
using global::Npgsql;
using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;

namespace Laplace.Chess.Service;

internal sealed record ChessIngestRunResult(
    Hash128 SourceId,
    string SourceName,
    int UnitsAttempted,
    int UnitsApplied,
    int UnitsFailed,
    int FilesDone,
    TimeSpan WallClock);

internal interface IChessIngestJournal
{
    Guid RunId { get; }
    void OnRunStart(string sourceName, int layerOrder);
    void OnFileStarted(string sourceName, string fileLabel);
    void OnFileFinished(string sourceName, string fileLabel, string status, string? error = null);
    void OnRunFinished(string sourceName, ChessIngestRunResult result, string status, string? error = null);
}

internal sealed class LoggingChessIngestJournal : IChessIngestJournal
{
    public Guid RunId { get; } = Guid.NewGuid();
    public void OnRunStart(string sourceName, int layerOrder) { }
    public void OnFileStarted(string sourceName, string fileLabel) { }
    public void OnFileFinished(string sourceName, string fileLabel, string status, string? error = null) { }
    public void OnRunFinished(string sourceName, ChessIngestRunResult result, string status, string? error = null) { }
}

/// <summary>
/// One web-triggered chess import (a Lab provider fetch, a gauntlet's games.pgn) as an ingest run: each
/// PGN file it admits is read by the Engine via <c>laplace witness</c>/<c>laplace ingest</c>, and every exit
/// path drives the run to one terminal status (ok, failed, cancelled) in host logs.
/// </summary>
internal sealed class ChessLabIngestRun
{
    private readonly IChessIngestJournal _journal;
    private readonly ILogger _log;
    private readonly string _jobId;
    private readonly string _label;
    private readonly string? _source;
    private readonly Func<string?, string, CancellationToken, Task> _ingest;
    private readonly Func<Guid> _runId;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private int _unitsDone, _unitsFailed;
    private bool _finished;

    internal ChessLabIngestRun(
        IChessIngestJournal journal, ILogger log, string jobId, string label, string? source,
        Func<string?, string, CancellationToken, Task> ingest, Func<Guid>? runId = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
        _jobId = jobId;
        _label = label;
        _source = source;
        _runId = runId ?? (() => _journal.RunId);
    }

    internal Guid RunId => _runId();

    /// <summary>The run's name in its receipts: the witness stating its files, or "pgn" for a file of no corpus.</summary>
    internal string SourceName => _source ?? "pgn";

    internal bool IsFinished { get { lock (_gate) return _finished; } }

    internal static async Task<ChessLabIngestRun> StartAsync(
        ChessLiveGameHost host, ILogger log, string jobId, string label, string? source, CancellationToken ct,
        string? trustClass = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ct.ThrowIfCancellationRequested();
        var journal = new LoggingChessIngestJournal();
        Func<string?, string, CancellationToken, Task> ingest = string.IsNullOrWhiteSpace(trustClass)
            ? host.IngestFileAsync
            : (witness, path, token) => host.IngestFileAsync(witness, path, trustClass, token);
        var run = new ChessLabIngestRun(journal, log, jobId, label, source, ingest, () => journal.RunId);
        await Task.Run(run.Start, CancellationToken.None).ConfigureAwait(false);
        return run;
    }

    internal void Start()
    {
        _journal.OnRunStart(SourceName, -1);
        _log.LogInformation(
            "CHESS_IMPORT_RUN_START job={JobId} run={RunId} source={Source} label={Label}",
            _jobId, RunId, SourceName, _label);
    }

    internal string FileLabel(string unit) => $"{_label}/{unit}";

    /// <summary>
    /// One PGN file as one file row: running, then ok/failed/cancelled with the error. A failure is
    /// rethrown unchanged; journaling only observes it.
    /// </summary>
    internal async Task IngestUnitAsync(string unit, string path, CancellationToken ct)
    {
        string file = FileLabel(unit);
        _journal.OnFileStarted(SourceName, file);
        try
        {
            await _ingest(_source, path, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            bool cancelled = ex is OperationCanceledException;
            Interlocked.Increment(ref _unitsFailed);
            _journal.OnFileFinished(SourceName, file, cancelled ? "cancelled" : "failed", Describe(ex));
            if (cancelled)
                _log.LogInformation("CHESS_IMPORT_UNIT job={JobId} run={RunId} unit={Unit} status=cancelled",
                    _jobId, RunId, file);
            else
                _log.LogWarning("CHESS_IMPORT_UNIT job={JobId} run={RunId} unit={Unit} status=failed error={Error}",
                    _jobId, RunId, file, Describe(ex));
            throw;
        }
        Interlocked.Increment(ref _unitsDone);
        _journal.OnFileFinished(SourceName, file, "ok");
        _log.LogInformation("CHESS_IMPORT_UNIT job={JobId} run={RunId} unit={Unit} status=ok", _jobId, RunId, file);
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
        int done = Volatile.Read(ref _unitsDone), failed = Volatile.Read(ref _unitsFailed);
        var result = new ChessIngestRunResult(
            Hash128.Zero,
            SourceName,
            UnitsAttempted: done + failed,
            UnitsApplied: done,
            UnitsFailed: failed,
            FilesDone: done,
            WallClock: _clock.Elapsed);
        _journal.OnRunFinished(SourceName, result, status, error);
        if (status == "failed")
            _log.LogWarning(
                "CHESS_IMPORT_RUN_END job={JobId} run={RunId} source={Source} status={Status} files={Files} "
                + "failed_files={Failed} elapsed_ms={Ms} error={Error}",
                _jobId, RunId, SourceName, status, done, failed, (long)_clock.Elapsed.TotalMilliseconds, error);
        else
            _log.LogInformation(
                "CHESS_IMPORT_RUN_END job={JobId} run={RunId} source={Source} status={Status} files={Files} "
                + "failed_files={Failed} elapsed_ms={Ms}",
                _jobId, RunId, SourceName, status, done, failed, (long)_clock.Elapsed.TotalMilliseconds);
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
