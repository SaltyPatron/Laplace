using global::Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Receipt ledger for ingest runs: <c>laplace.ingest_run_journal</c> (one row per run,
/// 'running' at start, driven to a terminal status on every exit path) and
/// <c>laplace.ingest_file_journal</c> (one row per artifact). Run-status writes are
/// synchronous; per-file events are queued and flushed as array-bound batches so file
/// workers never wait on journal I/O. The journal is operational metadata: a write
/// failure is logged and never aborts the ingest. One instance tracks one current run.
/// </summary>
public sealed class NpgsqlIngestObservability : IIngestObservability
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FileJournalFlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// classid of the per-run session advisory lock (objid = hashtext(run_id::text)).
    /// The server releases it when the holding session dies. <c>ops.ingest_run_live</c>
    /// treats a run as live if it holds this lock or has a fresh heartbeat, so losing one
    /// pooled connection does not orphan a running ingest.
    /// </summary>
    private const int RunLivenessLockClass = 0x4C504C4B; // "LPLK"

    private readonly NpgsqlDataSource _ds;
    private readonly bool _evidencePersisted;
    private readonly int _fileJournalFlushRows;
    private readonly ILogger? _log;
    private long _failureCount;
    private long _lostFileEvents;
    private Exception? _lastFailure;

    public long FailureCount => Interlocked.Read(ref _failureCount);
    public long LostFileEvents => Interlocked.Read(ref _lostFileEvents);
    public Exception? LastFailure => Volatile.Read(ref _lastFailure);

    private Guid _runId;
    private volatile bool _active;
    private DateTime _lastProgressUtc;
    private DateTime _lastHeartbeatUtc;
    private NpgsqlConnection? _livenessConn;
    private readonly System.Collections.Concurrent.ConcurrentQueue<FileJournalEvent> _fileEvents = new();
    private readonly object _fileFlushGate = new();
    private CancellationTokenSource? _filePumpCts;
    private Task? _filePump;

    private enum FileJournalEventKind : byte { Started, Progress, Composed, Finished }

    private readonly record struct FileJournalEvent(
        FileJournalEventKind Kind,
        string SourceName,
        string FileLabel,
        DateTimeOffset At,
        long Bytes = 0,
        DateTimeOffset? ModifiedAt = null,
        byte[]? FileId = null,
        byte[]? ResumeFingerprint = null,
        long Records = 0,
        long Entities = 0,
        long Physicalities = 0,
        long Attestations = 0,
        string? Status = null,
        string? Error = null);

    /// <summary>The ingest_run_journal key of the current run; <see cref="Guid.Empty"/> before OnRunStart.</summary>
    public Guid RunId => _runId;

    public void OnNativeRunStart(string sourceName, int layerOrder) =>
        StartRun(sourceName, null, layerOrder, null, null, unidentified: true);

    public void OnNativeInventory(int files) => Execute("journal.native_inventory", cmd =>
    {
        cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Add(new NpgsqlParameter { Value = (long)files });
    });

    public void OnNativeSourceIdentified(Hash128 sourceId) => Execute("journal.native_source", cmd =>
    {
        cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Add(new NpgsqlParameter { Value = sourceId.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
    });

    public void OnNativeFilesFinished(string status) => Execute("journal.native_files_finished", cmd =>
    {
        cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Add(new NpgsqlParameter { Value = status });
    });

    public void OnNativeAttestationMeasurement(bool measured) => Execute("journal.native_attestation_measurement", cmd =>
    {
        cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Add(new NpgsqlParameter { Value = measured });
    });

    public NpgsqlIngestObservability(NpgsqlDataSource dataSource, bool evidencePersisted = true, ILogger? logger = null)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _evidencePersisted = evidencePersisted;
        _log = logger;
        _fileJournalFlushRows = IngestSizing.ResolveTransitBatchRows(
            MemoryTopology.FileJournalTransitBytesPerEvent);
    }

    /// <summary>
    /// Receipt for one already-applied artifact as a complete one-file run. Unlike the
    /// queued run callbacks, the run row and the file row commit in one transaction before
    /// this returns, so the recorded size and modification time are durable once the
    /// caller reports success.
    /// </summary>
    public async Task RecordAcceptedArtifactAsync(
        string sourceName,
        Hash128 sourceId,
        string fileLabel,
        Hash128 fileId,
        long bytes,
        DateTimeOffset? modifiedAt,
        ApplyResult applied,
        TimeSpan wallClock,
        CancellationToken ct = default)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        ArgumentNullException.ThrowIfNull(applied);

        Guid runId = Guid.NewGuid();
        DateTimeOffset endedAt = DateTimeOffset.UtcNow;
        DateTimeOffset startedAt = endedAt - wallClock;
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(conn, transaction);

        var run = NpgsqlCatalog.Command(SqlCatalog.Get("journal.accepted_run"), parameters =>
        {
            parameters.Add(new NpgsqlParameter { Value = runId, NpgsqlDbType = NpgsqlDbType.Uuid });
            parameters.Add(new NpgsqlParameter { Value = sourceName, NpgsqlDbType = NpgsqlDbType.Text });
            parameters.Add(new NpgsqlParameter { Value = sourceId.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
            parameters.Add(new NpgsqlParameter { Value = startedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            parameters.Add(new NpgsqlParameter { Value = endedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            parameters.Add(new NpgsqlParameter { Value = (long)applied.EntitiesInserted, NpgsqlDbType = NpgsqlDbType.Bigint });
            parameters.Add(new NpgsqlParameter { Value = (long)applied.PhysicalitiesInserted, NpgsqlDbType = NpgsqlDbType.Bigint });
            parameters.Add(new NpgsqlParameter { Value = (long)applied.AttestationsInserted, NpgsqlDbType = NpgsqlDbType.Bigint });
            parameters.Add(new NpgsqlParameter { Value = Math.Max(1L, (long)Math.Round(wallClock.TotalMilliseconds)), NpgsqlDbType = NpgsqlDbType.Bigint });
            parameters.Add(new NpgsqlParameter { Value = _evidencePersisted, NpgsqlDbType = NpgsqlDbType.Boolean });
        });
        batch.BatchCommands.Add(run);

        var file = NpgsqlCatalog.Command(SqlCatalog.Get("journal.accepted_file"), parameters =>
        {
            parameters.Add(new NpgsqlParameter { Value = runId, NpgsqlDbType = NpgsqlDbType.Uuid });
            parameters.Add(new NpgsqlParameter { Value = fileLabel, NpgsqlDbType = NpgsqlDbType.Text });
            parameters.Add(new NpgsqlParameter { Value = sourceName, NpgsqlDbType = NpgsqlDbType.Text });
            parameters.Add(new NpgsqlParameter { Value = fileId.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
            parameters.Add(new NpgsqlParameter { Value = startedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            parameters.Add(new NpgsqlParameter { Value = endedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            parameters.Add(new NpgsqlParameter { Value = bytes, NpgsqlDbType = NpgsqlDbType.Bigint });
            parameters.Add(new NpgsqlParameter { Value = (object?)modifiedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            parameters.Add(new NpgsqlParameter { Value = (long)applied.EntitiesAttempted, NpgsqlDbType = NpgsqlDbType.Bigint });
            parameters.Add(new NpgsqlParameter { Value = (long)applied.PhysicalitiesAttempted, NpgsqlDbType = NpgsqlDbType.Bigint });
            parameters.Add(new NpgsqlParameter { Value = (long)applied.AttestationsAttempted, NpgsqlDbType = NpgsqlDbType.Bigint });
        });
        batch.BatchCommands.Add(file);

        await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public void OnRunStart(string sourceName, int layerOrder, IngestInventory? inventory) =>
        OnRunStart(sourceName, layerOrder, inventory, artifactGraph: null);

    public void OnRunStart(
        string sourceName,
        int layerOrder,
        IngestInventory? inventory,
        IngestArtifactGraph? artifactGraph) =>
        StartRun(sourceName, sourceId: null, layerOrder, inventory, artifactGraph);

    public void OnRunStart(
        string sourceName,
        Hash128 sourceId,
        int layerOrder,
        IngestInventory? inventory,
        IngestArtifactGraph? artifactGraph) =>
        StartRun(sourceName, sourceId.ToBytes(), layerOrder, inventory, artifactGraph);

    private void StartRun(
        string sourceName,
        byte[]? sourceId,
        int layerOrder,
        IngestInventory? inventory,
        IngestArtifactGraph? artifactGraph,
        bool unidentified = false)
    {
        _runId = Guid.NewGuid();
        _active = true;
        _lastProgressUtc = DateTime.MinValue;
        _lastHeartbeatUtc = DateTime.MinValue;
        ReconcileOrphanedRuns();
        // A caller that has the witness id records that id. source_id(name) is the
        // canonical substrate/source/<name>/v1 envelope, which is a different entity
        // from the [authority, release] witness attestations are filed under.
        bool journaled = Execute(
            unidentified ? "journal.run_start_unidentified"
                : sourceId is null ? "journal.run_start_named" : "journal.run_start_identified",
            cmd =>
            {
                cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Add(new NpgsqlParameter { Value = sourceName });
                if (sourceId is not null)
                    cmd.Add(new NpgsqlParameter { Value = sourceId, NpgsqlDbType = NpgsqlDbType.Bytea });
                cmd.Add(new NpgsqlParameter { Value = layerOrder });
                cmd.Add(new NpgsqlParameter { Value = (long)(inventory?.FileCount ?? 0) });
                cmd.Add(new NpgsqlParameter { Value = inventory?.TotalInputUnits ?? 0L });
                cmd.Add(new NpgsqlParameter { Value = _evidencePersisted });
            });
        if (journaled) WriteRequestedRunReceipt(sourceName, layerOrder);
        PersistArtifactInventory(sourceName, artifactGraph);
        AcquireLivenessLock();
        StartFileJournalPump();
    }

    private void WriteRequestedRunReceipt(string sourceName, int layerOrder)
    {
        string? path = Environment.GetEnvironmentVariable("LAPLACE_INGEST_RUN_RECEIPT_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        string staging = path + "." + _runId.ToString("N") + ".pending";
        try
        {
            // Written only after the journal INSERT commits, via a staged file and a
            // non-overwriting move, so an existing receipt at the path is never replaced.
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                run_id = _runId,
                source_name = sourceName,
                source_id = Convert.ToHexString(SubstrateCanonicalIds.Source(sourceName).ToBytes())
                    .ToLowerInvariant(),
                layer = layerOrder,
            });
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
                writer.Write(json);
            File.Move(staging, path, overwrite: false);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "INGEST_RUN_RECEIPT_WRITE_FAILED");
        }
    }

    private void PersistArtifactInventory(string sourceName, IngestArtifactGraph? artifactGraph)
    {
        if (artifactGraph is null || artifactGraph.Artifacts.Count == 0) return;

        try
        {
            using var conn = _ds.OpenConnection();
            using var transaction = conn.BeginTransaction();
            foreach (IngestArtifact[] artifacts in artifactGraph.Artifacts.Chunk(_fileJournalFlushRows))
            {
                using var cmd = NpgsqlCatalog.Command(conn, transaction, SqlCatalog.Get("journal.artifact_inventory"), parameters =>
                {
                parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                parameters.Add(new NpgsqlParameter { Value = sourceName, NpgsqlDbType = NpgsqlDbType.Text });
                parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.FileLabel).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.Id).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.RelativePath).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.DispositionName).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact =>
                            string.IsNullOrWhiteSpace(artifact.Notes) ? null : artifact.Notes)
                        .ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact =>
                            artifact.IsSelected ? "inventoried" : "not-selected")
                        .ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.Bytes ?? 0L).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                });
                parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.ModifiedAt).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
                });
                });
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (Exception ex)
        {
            LogFailure(ex, "INGEST_ARTIFACT_INVENTORY_WRITE_FAILED");
        }
    }

    /// <summary>
    /// Holds the per-run session advisory lock for the run's lifetime on a connection
    /// checked out of the ingest pool and not returned until the run ends, so idle pruning
    /// cannot reclaim it. That connection counts against
    /// PostgresResourcePlan.ObservabilityConnectionOwners together with the file-journal
    /// pump and the run-journal writer. Failure to acquire is logged and the ingest
    /// proceeds, but without the lock the run reads as orphaned once its heartbeat is stale.
    /// </summary>
    private void AcquireLivenessLock()
    {
        try
        {
            _livenessConn = _ds.OpenConnection();
            using var cmd = NpgsqlCatalog.Command(_livenessConn, null, "journal.liveness_lock", RunLivenessLockClass, _runId.ToString());
            cmd.ExecuteNonQuery();
            AcquireMeasurementLane(_livenessConn);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "INGEST_RUN_LIVENESS_LOCK_FAILED");
            _livenessConn?.Dispose();
            _livenessConn = null;
        }
    }

    /// <summary>
    /// Takes the measurement lane SHARED on the run's liveness connection, so the run holds
    /// it exactly as long as it can write and the server drops it when this process dies.
    ///
    /// <para>Blocks while a measurement holds the lane EXCLUSIVE, waiting in bounded
    /// windows and logging the holder each time. Unlike the liveness lock this wait is not
    /// optional: an ingest writing during a measurement would change what is measured.</para>
    /// </summary>
    private void AcquireMeasurementLane(NpgsqlConnection conn)
        => AdvisoryTxLock.HoldMeasurementLaneAsync(
                conn, exclusive: false,
                onWaiting: msg => Console.Error.WriteLine(
                    $"INGEST_WAITING_ON_MEASUREMENT_LANE run={_runId} — {msg}. This run does "
                    + "not write until the measurement finishes."),
                ct: CancellationToken.None)
            .GetAwaiter().GetResult();

    private void ReleaseLivenessLock()
    {
        _livenessConn?.Dispose();   // session end releases the advisory lock
        _livenessConn = null;
    }

    /// <summary>
    /// Closes runs that fail the heartbeat-or-lock liveness predicate, together with their
    /// unfinished file rows, via <c>ops.ingest_reconcile_orphans</c>.
    /// </summary>
    private void ReconcileOrphanedRuns() => Execute(
        "journal.reconcile_orphans",
        static _ => { });

    public void OnIntentApplied(string sourceName, ApplyResult result) { }

    public void OnIntentFailed(string sourceName, IngestFailure failure) { }

    public void OnProgress(IngestProgress progress)
        => WriteProgress(progress, force: false);

    public void OnNativeProgress(IngestProgress progress, bool force = false)
        => WriteProgress(progress, force);

    private void WriteProgress(IngestProgress progress, bool force)
    {
        if (!_active) return;
        var now = DateTime.UtcNow;
        if (!force && now - _lastProgressUtc < ProgressInterval) return;
        _lastProgressUtc = now;
        Execute(
            "journal.run_progress",
            cmd =>
            {
                cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Add(new NpgsqlParameter { Value = progress.UnitsAttempted });
                cmd.Add(new NpgsqlParameter { Value = progress.UnitsApplied });
                cmd.Add(new NpgsqlParameter { Value = progress.UnitsFailed });
                cmd.Add(new NpgsqlParameter { Value = progress.EntitiesInserted });
                cmd.Add(new NpgsqlParameter { Value = progress.PhysicalitiesInserted });
                cmd.Add(new NpgsqlParameter { Value = progress.AttestationsInserted });
                cmd.Add(new NpgsqlParameter { Value = (long)progress.FilesDone });
                cmd.Add(new NpgsqlParameter { Value = progress.InputUnitsDone });
                cmd.Add(new NpgsqlParameter { Value = (long)progress.ConsensusBackendWork.TotalMilliseconds });
                cmd.Add(new NpgsqlParameter { Value = (long)progress.HighwayMaskBackendWork.TotalMilliseconds });
                cmd.Add(new NpgsqlParameter { Value = progress.ConsensusCalls });
                cmd.Add(new NpgsqlParameter { Value = progress.HighwayMaskCalls });
                cmd.Add(new NpgsqlParameter { Value = progress.HighwayMaskPairs });
            });
    }

    public void OnCompletionPhase(string sourceName, BulkRunCompletionPhase phase)
    {
        if (!_active) return;
        string value = phase switch
        {
            BulkRunCompletionPhase.ConsensusDrain => "consensus-drain",
            BulkRunCompletionPhase.WriterMaintenance => "writer-maintenance",
            _ => "finalizing",
        };
        Execute(
            "journal.run_phase",
            cmd =>
            {
                cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Add(new NpgsqlParameter { Value = value });
            });
    }

    public void OnBulkCompletion(
        string sourceName, TimeSpan foldDrain, TimeSpan writerMaintenance,
        TimeSpan foldSpan, TimeSpan consensusBackendWork, TimeSpan highwayMaskBackendWork,
        long consensusCalls, long highwayMaskCalls, long highwayMaskPairs)
    {
        if (!_active) return;
        Execute(
            "journal.run_completion",
            cmd =>
            {
                cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Add(new NpgsqlParameter { Value = (long)foldDrain.TotalMilliseconds });
                cmd.Add(new NpgsqlParameter { Value = (long)writerMaintenance.TotalMilliseconds });
                cmd.Add(new NpgsqlParameter { Value = (long)foldSpan.TotalMilliseconds });
                cmd.Add(new NpgsqlParameter { Value = (long)consensusBackendWork.TotalMilliseconds });
                cmd.Add(new NpgsqlParameter { Value = (long)highwayMaskBackendWork.TotalMilliseconds });
                cmd.Add(new NpgsqlParameter { Value = consensusCalls });
                cmd.Add(new NpgsqlParameter { Value = highwayMaskCalls });
                cmd.Add(new NpgsqlParameter { Value = highwayMaskPairs });
            });
    }

    public void OnRunFinished(string sourceName, IngestRunResult result, string status, string? error = null)
    {
        if (!_active) return;
        _active = false;
        StopFileJournalPump();
        // The error is written in this terminal UPDATE: OnRunFailed returns early once
        // the run is terminal, so it cannot attach the error afterwards.
        Execute(
            "journal.run_finished",
            cmd =>
            {
                cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Add(new NpgsqlParameter { Value = status });
                cmd.Add(new NpgsqlParameter { Value = result.UnitsAttempted });
                cmd.Add(new NpgsqlParameter { Value = result.UnitsApplied });
                cmd.Add(new NpgsqlParameter { Value = result.UnitsFailed });
                cmd.Add(new NpgsqlParameter { Value = result.EntitiesInserted });
                cmd.Add(new NpgsqlParameter { Value = result.PhysicalitiesInserted });
                cmd.Add(new NpgsqlParameter { Value = result.AttestationsInserted });
                cmd.Add(new NpgsqlParameter
                {
                    Value = (object?)error ?? DBNull.Value,
                    NpgsqlDbType = NpgsqlDbType.Text,
            });
                cmd.Add(new NpgsqlParameter { Value = (long)result.FilesDone });
                cmd.Add(new NpgsqlParameter { Value = result.InputUnitsDone });
                cmd.Add(new NpgsqlParameter { Value = result.InputUnitsTotal });
                cmd.Add(new NpgsqlParameter
                {
                    Value = Math.Max(1L, (long)Math.Round(result.WallClock.TotalMilliseconds)),
                    NpgsqlDbType = NpgsqlDbType.Bigint,
                });
                });

        ReportThroughputVerdict(sourceName);
        ReportEntityPhysicalityInsertDelta(sourceName, result);
        ReleaseLivenessLock();
    }

    /// <summary>
    /// Reads back the throughput verdict that the journal trigger computed on the terminal
    /// transition and prints it as the run's INGEST_THROUGHPUT receipt line; no throughput
    /// is recalculated here.
    /// </summary>
    private void ReportThroughputVerdict(string sourceName)
    {
        try
        {
            using var conn = _ds.OpenConnection();
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
                using var conn = _ds.OpenConnection();
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
            using var conn = _ds.OpenConnection();
            using var cmd = NpgsqlCatalog.Command(conn, null, SqlCatalog.Get(queryName), bind);
            cmd.ExecuteNonQuery();
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
