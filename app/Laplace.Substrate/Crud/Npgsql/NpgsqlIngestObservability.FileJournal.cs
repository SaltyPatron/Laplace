using global::Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class NpgsqlIngestObservability
{
    /// <summary>
    /// Reads back the throughput verdict that the journal trigger computed on the terminal
    /// transition and prints it as the run's INGEST_THROUGHPUT receipt line; no throughput
    /// is recalculated here.
    /// </summary>
    private void ReportThroughputVerdict(string sourceName)
    {
        try
        {
            WithJournalConnection(conn =>
            {
                using var cmd = NpgsqlCatalog.Command(conn, null, "journal.throughput_verdict", _runId);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    LogFailure(new InvalidOperationException("The ingest journal row is missing."),
                        "INGEST_THROUGHPUT_READ_FAILED");
                    return;
                }

                string runStatus = reader.GetString(0);
                if (runStatus == "running")
                    return; // The terminal UPDATE already emitted INGEST_RUN_JOURNAL_WRITE_FAILED.

                string verdict = reader.GetString(1);
                bool compared = reader.GetBoolean(2);
                string rows = reader.IsDBNull(3) ? "-" : reader.GetInt64(3).ToString();
                string elapsedMs = reader.IsDBNull(4) ? "-" : reader.GetInt64(4).ToString();
                string rate = reader.IsDBNull(5)
                    ? "-" : reader.GetDouble(5).ToString("0.0", CultureInfo.InvariantCulture);
                string baseline = reader.IsDBNull(6)
                    ? "-" : reader.GetDouble(6).ToString("0.0", CultureInfo.InvariantCulture);
                string slowdown = reader.IsDBNull(7)
                    ? "-" : reader.GetDouble(7).ToString("0.00", CultureInfo.InvariantCulture);
                string receipt = $"source={sourceName} run={_runId} verdict={verdict} "
                    + $"compared={(compared ? 1 : 0)} rows={rows} elapsed_ms={elapsedMs} "
                    + $"rate_rows_s={rate} baseline_rows_s={baseline} slowdown={slowdown}";

                Console.WriteLine($"INGEST_THROUGHPUT {receipt}");
                if (verdict is "slow")
                    Console.Error.WriteLine($"INGEST_THROUGHPUT_REJECTED {receipt}");
            });
        }
        catch (Exception ex)
        {
            LogFailure(ex, "INGEST_THROUGHPUT_READ_FAILED");
        }
    }

    /// <summary>
    /// Diagnostic only. Per-run insert counts are not a closure proof: content that already
    /// converged to an existing entity or physicality inserts zero rows in this run.
    /// </summary>
    private void ReportEntityPhysicalityInsertDelta(string sourceName, IngestRunResult result)
    {
        long delta = result.PhysicalitiesInserted - result.EntitiesInserted;
        if (delta == 0) return;
        Console.Error.WriteLine(
            $"INGEST_ENTITY_PHYSICALITY_INSERT_DELTA source={sourceName} run={_runId} "
            + $"entities={result.EntitiesInserted} physicalities={result.PhysicalitiesInserted} "
            + $"delta={delta} diagnostic_only=1");
    }

    public void OnRunFailed(string sourceName, string status, string error)
    {
        if (_active)
        {
            _active = false;
            StopFileJournalPump();
            Execute(
                "journal.run_failed",
                cmd =>
                {
                    cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                    cmd.Add(new NpgsqlParameter { Value = status });
                    cmd.Add(new NpgsqlParameter { Value = error });
                });
            ReleaseLivenessLock();
            return;
        }
        // A failure before OnRunStart (initialization or inventory) gets its own terminal
        // row. After OnRunFinished the run is already terminal and this is a no-op.
        if (_runId != Guid.Empty) return;
        Execute(
            "journal.failed_before_start",
            cmd =>
            {
                cmd.Add(new NpgsqlParameter { Value = Guid.NewGuid(), NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Add(new NpgsqlParameter { Value = sourceName });
                cmd.Add(new NpgsqlParameter { Value = status });
                cmd.Add(new NpgsqlParameter { Value = _evidencePersisted });
                cmd.Add(new NpgsqlParameter { Value = error });
            });
    }

    public void OnRunSkipped(string sourceName, int layerOrder) =>
        InsertSkipped(sourceName, sourceId: null, layerOrder);

    public void OnRunSkipped(string sourceName, Hash128 sourceId, int layerOrder) =>
        InsertSkipped(sourceName, sourceId.ToBytes(), layerOrder);

    private void InsertSkipped(string sourceName, byte[]? sourceId, int layerOrder)
    {
        Execute(
            sourceId is null ? "journal.run_skip_named" : "journal.run_skip_identified",
            cmd =>
            {
                cmd.Add(new NpgsqlParameter { Value = Guid.NewGuid(), NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Add(new NpgsqlParameter { Value = sourceName });
                if (sourceId is not null)
                    cmd.Add(new NpgsqlParameter { Value = sourceId, NpgsqlDbType = NpgsqlDbType.Bytea });
                cmd.Add(new NpgsqlParameter { Value = layerOrder });
                cmd.Add(new NpgsqlParameter { Value = _evidencePersisted });
            });
    }

    /// <summary>
    /// Per-file ledger rows (<c>laplace.ingest_file_journal</c>). Calls only enqueue; the
    /// file journal pump flushes them in batches.
    ///
    /// Started upserts and resets the row: a file re-opened within one run is the same
    /// unit, and the latest attempt decides its outcome.
    /// </summary>
    public void OnFileStarted(string sourceName, string fileLabel, long bytes = 0) =>
        OnFileStarted(sourceName, fileLabel, bytes, modifiedAt: null);

    public void OnFileStarted(
        string sourceName, string fileLabel, long bytes, DateTimeOffset? modifiedAt)
    {
        if (!_active) return;
        _fileEvents.Enqueue(new FileJournalEvent(
            FileJournalEventKind.Started, sourceName, fileLabel, DateTimeOffset.UtcNow,
            Bytes: bytes, ModifiedAt: modifiedAt));
    }

    public void OnFileFinished(
        string sourceName, string fileLabel, string status, string? error = null)
    {
        if (!_active) return;
        _fileEvents.Enqueue(new FileJournalEvent(
            FileJournalEventKind.Finished, sourceName, fileLabel, DateTimeOffset.UtcNow,
            Status: status, Error: error));
    }

    /// <summary>
    /// Counter update for a file still in flight. It never inserts or changes status; the
    /// UPDATE is guarded on status = 'running', so a progress event that loses a race with
    /// Composed or a terminal status is discarded instead of overwriting a finished row.
    /// </summary>
    public void OnFileProgress(
        string sourceName, string fileLabel,
        long records, long entities, long physicalities, long attestations)
    {
        if (!_active) return;
        _fileEvents.Enqueue(new FileJournalEvent(
            FileJournalEventKind.Progress, sourceName, fileLabel, DateTimeOffset.UtcNow,
            Records: records, Entities: entities,
            Physicalities: physicalities, Attestations: attestations));
    }

    public void OnFileComposed(
        string sourceName, string fileLabel, Hash128? fileId = null,
        long records = 0, long entities = 0, long physicalities = 0, long attestations = 0,
        Hash128? resumeFingerprint = null)
    {
        if (!_active) return;
        _fileEvents.Enqueue(new FileJournalEvent(
            FileJournalEventKind.Composed, sourceName, fileLabel, DateTimeOffset.UtcNow,
            FileId: fileId?.ToBytes(), ResumeFingerprint: resumeFingerprint?.ToBytes(),
            Records: records, Entities: entities,
            Physicalities: physicalities, Attestations: attestations));
    }

    private void StartFileJournalPump()
    {
        while (_fileEvents.TryDequeue(out _)) { }
        _filePumpCts?.Dispose();
        _filePumpCts = new CancellationTokenSource();
        var token = _filePumpCts.Token;
        _filePump = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(FileJournalFlushInterval, token).ConfigureAwait(false);
                    FlushFileJournalEvents();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { LogFailure(ex, "INGEST_FILE_JOURNAL_PUMP_FAILED"); }
        }, CancellationToken.None);
    }

    private void StopFileJournalPump()
    {
        var cts = _filePumpCts;
        var pump = _filePump;
        _filePumpCts = null;
        _filePump = null;
        if (cts is not null)
        {
            cts.Cancel();
            try { pump?.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                LogFailure(ex, "INGEST_FILE_JOURNAL_PUMP_FAILED");
            }
            cts.Dispose();
        }
        while (!_fileEvents.IsEmpty)
            FlushFileJournalEvents();
    }

    /// <summary>
    /// Drains queued file events into one batch: an optional run heartbeat, then one
    /// array-bound statement per event kind in lifecycle order (started, progress,
    /// composed, finished). Grouping by kind is safe because a file's events arrive in
    /// that order; a finished event with no started row inserts its own row.
    /// </summary>
    private void FlushFileJournalEvents()
    {
        lock (_fileFlushGate)
        {
            var events = new List<FileJournalEvent>(_fileJournalFlushRows);
            while (events.Count < _fileJournalFlushRows && _fileEvents.TryDequeue(out var evt))
                events.Add(evt);
            DateTime now = DateTime.UtcNow;
            bool heartbeatDue = _active && now - _lastHeartbeatUtc >= ProgressInterval;
            if (events.Count == 0 && !heartbeatDue) return;

            try
            {
                WithJournalConnection(conn =>
                {
                    using var batch = new NpgsqlBatch(conn);
                    // The heartbeat rides the pump's batch, so a run with no file events
                    // still refreshes liveness without another connection owner.
                    if (heartbeatDue)
                    {
                        var heartbeat = NpgsqlCatalog.Command(SqlCatalog.Get("journal.heartbeat"), parameters =>
                        {
                            parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                        });
                        batch.BatchCommands.Add(heartbeat);
                    }

                    var started = events.Where(e => e.Kind == FileJournalEventKind.Started).ToArray();
                    if (started.Length > 0)
                    {
                        var cmd = NpgsqlCatalog.Command(SqlCatalog.Get("journal.files_started"), parameters =>
                        {
                            parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                            parameters.Add(new NpgsqlParameter { Value = started.Select(e => e.FileLabel).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                            parameters.Add(new NpgsqlParameter { Value = started.Select(e => e.SourceName).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                            parameters.Add(new NpgsqlParameter { Value = started.Select(e => e.Bytes).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                            parameters.Add(new NpgsqlParameter { Value = started.Select(e => e.ModifiedAt).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                            parameters.Add(new NpgsqlParameter { Value = started.Select(e => e.At).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                        });
                        batch.BatchCommands.Add(cmd);
                    }

                    // Counters are cumulative, so only the last progress event per file in
                    // this flush is sent.
                    var progress = events
                        .Where(e => e.Kind == FileJournalEventKind.Progress)
                        .GroupBy(e => e.FileLabel, StringComparer.Ordinal)
                        .Select(g => g.Last())
                        .ToArray();
                    if (progress.Length > 0)
                    {
                        // UPDATE only: Started creates the row, and status = 'running' keeps a
                        // late progress event from overwriting a composed or terminal row.
                        var cmd = NpgsqlCatalog.Command(SqlCatalog.Get("journal.files_progress"), parameters =>
                        {
                            parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                            parameters.Add(new NpgsqlParameter { Value = progress.Select(e => e.FileLabel).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                            parameters.Add(new NpgsqlParameter { Value = progress.Select(e => e.Records).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                            parameters.Add(new NpgsqlParameter { Value = progress.Select(e => e.Entities).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                            parameters.Add(new NpgsqlParameter { Value = progress.Select(e => e.Physicalities).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                            parameters.Add(new NpgsqlParameter { Value = progress.Select(e => e.Attestations).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                        });
                        batch.BatchCommands.Add(cmd);
                    }

                    var composed = events.Where(e => e.Kind == FileJournalEventKind.Composed).ToArray();
                    if (composed.Length > 0)
                    {
                        var cmd = NpgsqlCatalog.Command(SqlCatalog.Get("journal.files_composed"), parameters =>
                        {
                            parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                            parameters.Add(new NpgsqlParameter { Value = composed.Select(e => e.FileLabel).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                            parameters.Add(new NpgsqlParameter { Value = composed.Select(e => e.SourceName).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                            parameters.Add(new NpgsqlParameter { Value = composed.Select(e => e.FileId).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                            parameters.Add(new NpgsqlParameter { Value = composed.Select(e => e.ResumeFingerprint).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
                            parameters.Add(new NpgsqlParameter { Value = composed.Select(e => e.Records).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                            parameters.Add(new NpgsqlParameter { Value = composed.Select(e => e.Entities).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                            parameters.Add(new NpgsqlParameter { Value = composed.Select(e => e.Physicalities).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                            parameters.Add(new NpgsqlParameter { Value = composed.Select(e => e.Attestations).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
                            parameters.Add(new NpgsqlParameter { Value = composed.Select(e => e.At).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                        });
                        batch.BatchCommands.Add(cmd);
                    }

                    var finished = events.Where(e => e.Kind == FileJournalEventKind.Finished).ToArray();
                    if (finished.Length > 0)
                    {
                        var cmd = NpgsqlCatalog.Command(SqlCatalog.Get("journal.files_finished"), parameters =>
                        {
                            parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                            parameters.Add(new NpgsqlParameter { Value = finished.Select(e => e.FileLabel).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                            parameters.Add(new NpgsqlParameter { Value = finished.Select(e => e.SourceName).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                            parameters.Add(new NpgsqlParameter { Value = finished.Select(e => e.Status!).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                            parameters.Add(new NpgsqlParameter { Value = finished.Select(e => e.At).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
                            parameters.Add(new NpgsqlParameter { Value = finished.Select(e => e.Error).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                        });
                        batch.BatchCommands.Add(cmd);
                    }

                    batch.ExecuteNonQuery();
                });
                if (heartbeatDue) _lastHeartbeatUtc = now;
            }
            catch (Exception ex)
            {
                Interlocked.Add(ref _lostFileEvents, events.Count);
                LogFailure(ex, "INGEST_FILE_JOURNAL_WRITE_FAILED");
            }
        }
    }

    private bool Execute(string queryName, Action<NpgsqlParameterCollection> bind, string failTag = "INGEST_RUN_JOURNAL_WRITE_FAILED")
    {
        try
        {
            WithJournalConnection(conn =>
            {
                using var cmd = NpgsqlCatalog.Command(conn, null, SqlCatalog.Get(queryName), bind);
                cmd.ExecuteNonQuery();
            });
            return true;
        }
        catch (Exception ex)
        {
            LogFailure(ex, failTag);
            return false;
        }
    }

    private void LogFailure(Exception exception, string eventName)
    {
        Volatile.Write(ref _lastFailure, exception);
        long count = Interlocked.Increment(ref _failureCount);
        if (_log is not null)
            _log.LogError(exception,
                "{FailureEvent} run={RunId} failure_count={FailureCount} file_events_lost={LostFileEvents}",
                eventName, _runId, count, LostFileEvents);
        else
            // Library consumers without a logger still retain the complete causal stack.
            // Process hosts bridge Trace into their persistent structured sinks.
            Trace.TraceError("{0} run={1} failure_count={2} file_events_lost={3}: {4}",
                eventName, _runId, count, LostFileEvents, exception);
    }
}
