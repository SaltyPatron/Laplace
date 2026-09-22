using global::Npgsql;
using NpgsqlTypes;
using System.Globalization;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Persists the ingest run ledger (laplace.ingest_run_journal): one row per run,
/// 'running' at start, driven to a terminal status on every exit path. Run-status writes
/// are synchronous; high-cardinality file events queue and flush in array-backed batches
/// so file workers never block on journal I/O. Journaling is ops metadata, so a write
/// failure logs loudly and never aborts the ingest itself. One runner drives one run at a
/// time (the ingest mutex), so a single current-run id suffices.
/// </summary>
public sealed class NpgsqlIngestObservability : IIngestObservability
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FileJournalFlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// classid of the per-run session advisory lock (objid = hashtext(run_id::text)).
    /// The server releases this beacon when its session dies. Canonical liveness
    /// in ops.ingest_run_live accepts either this beacon or a fresh heartbeat;
    /// losing a pooled connection must not cancel a still-running ingest.
    /// Both managed reconciliation and deployment consult that SQL predicate.
    /// </summary>
    private const int RunLivenessLockClass = 0x4C504C4B; // "LPLK"

    private readonly NpgsqlDataSource _ds;
    private readonly bool _evidencePersisted;
    private readonly int _fileJournalFlushRows;

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

    public NpgsqlIngestObservability(NpgsqlDataSource dataSource, bool evidencePersisted = true)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _evidencePersisted = evidencePersisted;
        _fileJournalFlushRows = IngestSizing.ResolveTransitBatchRows(
            MemoryTopology.FileJournalTransitBytesPerEvent);
    }

    /// <summary>
    /// Persist one already-accepted runtime artifact as a complete one-file run. Unlike the
    /// asynchronous ingest-run callbacks, this method is an admission boundary: it returns only
    /// after the run and occurrence rows commit together, so a successful API response cannot
    /// silently lose the supplied size or modification-time observation.
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

        var run = new NpgsqlBatchCommand(
            "INSERT INTO laplace.ingest_run_journal "
            + "(run_id, source_name, source_id, layer, status, phase, started_at, ended_at, "
            + "units_attempted, units_applied, entities, physicalities, attestations, "
            + "files_done, files_total, input_units_done, input_units_total, "
            + "throughput_elapsed_ms, evidence_persisted) "
            + "VALUES ($1, $2, $3, 0, 'ok', 'complete', $4, $5, "
            + "1, 1, $6, $7, $8, 1, 1, 1, 1, $9, $10)");
        AddParameter(run, runId, NpgsqlDbType.Uuid);
        AddParameter(run, sourceName, NpgsqlDbType.Text);
        AddParameter(run, sourceId.ToBytes(), NpgsqlDbType.Bytea);
        AddParameter(run, startedAt, NpgsqlDbType.TimestampTz);
        AddParameter(run, endedAt, NpgsqlDbType.TimestampTz);
        AddParameter(run, (long)applied.EntitiesInserted, NpgsqlDbType.Bigint);
        AddParameter(run, (long)applied.PhysicalitiesInserted, NpgsqlDbType.Bigint);
        AddParameter(run, (long)applied.AttestationsInserted, NpgsqlDbType.Bigint);
        AddParameter(run, Math.Max(1L, (long)Math.Round(wallClock.TotalMilliseconds)), NpgsqlDbType.Bigint);
        AddParameter(run, _evidencePersisted, NpgsqlDbType.Boolean);
        batch.BatchCommands.Add(run);

        var file = new NpgsqlBatchCommand(
            "INSERT INTO laplace.ingest_file_journal "
            + "(run_id, file_label, source_name, file_id, status, started_at, ended_at, "
            + "bytes, modified_at, records, entities, physicalities, attestations) "
            + "VALUES ($1, $2, $3, $4, 'ok', $5, $6, $7, $8, 1, $9, $10, $11)");
        AddParameter(file, runId, NpgsqlDbType.Uuid);
        AddParameter(file, fileLabel, NpgsqlDbType.Text);
        AddParameter(file, sourceName, NpgsqlDbType.Text);
        AddParameter(file, fileId.ToBytes(), NpgsqlDbType.Bytea);
        AddParameter(file, startedAt, NpgsqlDbType.TimestampTz);
        AddParameter(file, endedAt, NpgsqlDbType.TimestampTz);
        AddParameter(file, bytes, NpgsqlDbType.Bigint);
        AddParameter(file, (object?)modifiedAt ?? DBNull.Value, NpgsqlDbType.TimestampTz);
        AddParameter(file, (long)applied.EntitiesAttempted, NpgsqlDbType.Bigint);
        AddParameter(file, (long)applied.PhysicalitiesAttempted, NpgsqlDbType.Bigint);
        AddParameter(file, (long)applied.AttestationsAttempted, NpgsqlDbType.Bigint);
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
        IngestArtifactGraph? artifactGraph)
    {
        _runId = Guid.NewGuid();
        _active = true;
        _lastProgressUtc = DateTime.MinValue;
        _lastHeartbeatUtc = DateTime.MinValue;
        ReconcileOrphanedRuns();
        bool journaled = Execute(
            "INSERT INTO laplace.ingest_run_journal "
            + "(run_id, source_name, source_id, layer, status, files_total, input_units_total, evidence_persisted) "
            + "VALUES ($1, $2, laplace.source_id($2), $3, 'running', $4, $5, $6)",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = sourceName });
                cmd.Parameters.Add(new NpgsqlParameter { Value = layerOrder });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)(inventory?.FileCount ?? 0) });
                cmd.Parameters.Add(new NpgsqlParameter { Value = inventory?.TotalInputUnits ?? 0L });
                cmd.Parameters.Add(new NpgsqlParameter { Value = _evidencePersisted });
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
            // A caller owns a fresh path for this invocation. Publish only after the
            // journal INSERT commits; never replace an older invocation's receipt.
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
            Console.Error.WriteLine(
                $"INGEST_RUN_RECEIPT_WRITE_FAILED run={_runId} error=[{ex.GetType().Name}] {ex.Message}");
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
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText =
                    "INSERT INTO laplace.ingest_file_journal AS current_file "
                    + "(run_id, file_label, source_name, artifact_id, relative_path, disposition, "
                    + "disposition_reason, status, bytes, modified_at, started_at, ended_at) "
                    + "SELECT $1, u.file_label, $2, u.artifact_id, u.relative_path, u.disposition, "
                    + "u.reason, u.status, u.bytes, u.modified_at, now(), "
                    + "CASE WHEN u.status = 'not-selected' THEN now() ELSE NULL END "
                    + "FROM unnest($3, $4, $5, $6, $7, $8, $9, $10) "
                    + "AS u(file_label, artifact_id, relative_path, disposition, reason, status, bytes, modified_at) "
                    + "ON CONFLICT (run_id, file_label) DO UPDATE SET "
                    + "artifact_id = EXCLUDED.artifact_id, relative_path = EXCLUDED.relative_path, "
                    + "disposition = EXCLUDED.disposition, disposition_reason = EXCLUDED.disposition_reason, "
                    + "status = EXCLUDED.status, bytes = EXCLUDED.bytes, modified_at = EXCLUDED.modified_at, "
                    + "ended_at = EXCLUDED.ended_at";
                cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = sourceName, NpgsqlDbType = NpgsqlDbType.Text });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.FileLabel).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.Id).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.RelativePath).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.DispositionName).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact =>
                            string.IsNullOrWhiteSpace(artifact.Notes) ? null : artifact.Notes)
                        .ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact =>
                            artifact.IsSelected ? "inventoried" : "not-selected")
                        .ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.Bytes ?? 0L).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = artifacts.Select(static artifact => artifact.ModifiedAt).ToArray(),
                    NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
                });
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"INGEST_ARTIFACT_INVENTORY_WRITE_FAILED run={_runId} "
                + $"error=[{ex.GetType().Name}] {ex.Message}");
        }
    }

    /// <summary>
    /// Hold the per-run session advisory lock for the run's lifetime on a dedicated
    /// connection. It is CHECKED OUT OF THE INGEST POOL and never returned until the
    /// run ends, so it is a permanent pool owner, not -- as this comment previously
    /// claimed -- a connection "pinned outside the pool". Idle pruning cannot reclaim
    /// it precisely because it is never returned, so the lock is safe under a live
    /// run; what it is not is free. PostgresResourcePlan.ObservabilityConnectionOwners
    /// budgets this slot together with the file-journal pump and the run-journal
    /// writer. Do not add another connection owner to this class without moving that
    /// number. Failure to acquire logs loudly and never aborts
    /// the ingest — same law as every other journal write in this class — but a
    /// run without the lock is indistinguishable from a corpse to the deploy
    /// gate, so the log line matters.
    /// </summary>
    private void AcquireLivenessLock()
    {
        try
        {
            _livenessConn = _ds.OpenConnection();
            using var cmd = _livenessConn.CreateCommand();
            cmd.CommandText = "SELECT pg_advisory_lock($1, hashtext($2))";
            cmd.Parameters.Add(new NpgsqlParameter { Value = RunLivenessLockClass });
            cmd.Parameters.Add(new NpgsqlParameter { Value = _runId.ToString() });
            cmd.ExecuteNonQuery();
            AcquireMeasurementLane(_livenessConn);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"INGEST_RUN_LIVENESS_LOCK_FAILED run={_runId} — {ex.Message}; the run "
                + "proceeds, but wait-for-quiet-substrate.sh will read it as orphaned");
            _livenessConn?.Dispose();
            _livenessConn = null;
        }
    }

    /// <summary>
    /// Take the measurement lane SHARED on the run's liveness connection, so the run
    /// holds it for exactly as long as it can write and the server drops it the instant
    /// this process dies.
    ///
    /// <para>Blocks while a measurement holds the lane EXCLUSIVE — that is the contract,
    /// not a stall. It blocks in bounded windows and names the holder each time, because
    /// the failure this must never reproduce is AdvisoryTxLock's original form: an
    /// unbounded silent wait that presented as "ingest hung at the end" with nothing to
    /// act on. An unlocked ingest would silently corrupt every number a measurement is
    /// taking, so unlike the liveness beacon this one is not optional — it waits.</para>
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
    /// Use the same heartbeat-plus-beacon predicate as Operator and deployment.
    /// The canonical operation closes the orphan and its unfinished files together.
    /// </summary>
    private void ReconcileOrphanedRuns() => Execute(
        "SELECT * FROM ops.ingest_reconcile_orphans(interval '90 seconds')",
        static _ => { });

    public void OnIntentApplied(string sourceName, ApplyResult result) { }

    public void OnIntentFailed(string sourceName, IngestFailure failure) { }

    public void OnProgress(IngestProgress progress)
    {
        if (!_active) return;
        var now = DateTime.UtcNow;
        if (now - _lastProgressUtc < ProgressInterval) return;
        _lastProgressUtc = now;
        Execute(
            "UPDATE laplace.ingest_run_journal SET "
            + "units_attempted = $2, units_applied = $3, units_failed = $4, "
            + "entities = $5, physicalities = $6, attestations = $7, "
            + "files_done = $8, input_units_done = $9, "
            + "consensus_backend_ms = $10, highway_mask_backend_ms = $11, "
            + "consensus_calls = $12, highway_mask_calls = $13, highway_mask_pairs = $14 "
            + "WHERE run_id = $1",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = progress.UnitsAttempted });
                cmd.Parameters.Add(new NpgsqlParameter { Value = progress.UnitsApplied });
                cmd.Parameters.Add(new NpgsqlParameter { Value = progress.UnitsFailed });
                cmd.Parameters.Add(new NpgsqlParameter { Value = progress.EntitiesInserted });
                cmd.Parameters.Add(new NpgsqlParameter { Value = progress.PhysicalitiesInserted });
                cmd.Parameters.Add(new NpgsqlParameter { Value = progress.AttestationsInserted });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)progress.FilesDone });
                cmd.Parameters.Add(new NpgsqlParameter { Value = progress.InputUnitsDone });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)progress.ConsensusBackendWork.TotalMilliseconds });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)progress.HighwayMaskBackendWork.TotalMilliseconds });
                cmd.Parameters.Add(new NpgsqlParameter { Value = progress.ConsensusCalls });
                cmd.Parameters.Add(new NpgsqlParameter { Value = progress.HighwayMaskCalls });
                cmd.Parameters.Add(new NpgsqlParameter { Value = progress.HighwayMaskPairs });
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
            "UPDATE laplace.ingest_run_journal SET phase = $2 WHERE run_id = $1",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = value });
            });
    }

    public void OnBulkCompletion(
        string sourceName, TimeSpan foldDrain, TimeSpan writerMaintenance,
        TimeSpan foldSpan, TimeSpan consensusBackendWork, TimeSpan highwayMaskBackendWork,
        long consensusCalls, long highwayMaskCalls, long highwayMaskPairs)
    {
        if (!_active) return;
        Execute(
            "UPDATE laplace.ingest_run_journal SET fold_drain_ms = $2, writer_maintenance_ms = $3, "
            + "fold_span_ms = $4, consensus_backend_ms = $5, highway_mask_backend_ms = $6, "
            + "consensus_calls = $7, highway_mask_calls = $8, highway_mask_pairs = $9 "
            + "WHERE run_id = $1",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)foldDrain.TotalMilliseconds });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)writerMaintenance.TotalMilliseconds });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)foldSpan.TotalMilliseconds });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)consensusBackendWork.TotalMilliseconds });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)highwayMaskBackendWork.TotalMilliseconds });
                cmd.Parameters.Add(new NpgsqlParameter { Value = consensusCalls });
                cmd.Parameters.Add(new NpgsqlParameter { Value = highwayMaskCalls });
                cmd.Parameters.Add(new NpgsqlParameter { Value = highwayMaskPairs });
            });
    }

    public void OnRunFinished(string sourceName, IngestRunResult result, string status, string? error = null)
    {
        if (!_active) return;
        _active = false;
        StopFileJournalPump();
        // error is written HERE, not by a follow-up OnRunFailed: that method returns early
        // once the run is terminal, so every failure reaching this path landed in the ledger
        // as status=failed with error NULL. MEASURED 2026-08-10: the document lane recorded
        // failed at files_done=199/207 twice, with no diagnostic in the row either time.
        Execute(
            "UPDATE laplace.ingest_run_journal SET "
            + "status = $2, phase = CASE WHEN $2 = 'failed' THEN 'failed' ELSE 'complete' END, "
            + "ended_at = now(), "
            // Persist the same runner stopwatch that emits INGEST_COMPLETE elapsed_s.
            // started_at is journal-entry time, after decomposer initialization/inventory,
            // so deriving throughput from ended_at-started_at would compare a shorter
            // clock against the historical runner-clock baselines migrated by #1080.
            + "throughput_elapsed_ms = $13, "
            + "units_attempted = $3, units_applied = $4, units_failed = $5, "
            + "entities = $6, physicalities = $7, attestations = $8, "
            + "error = COALESCE($9, error), "
            // File and input counters rode ONLY the throttled progress UPDATE, so fast
            // runs routinely ended with stale ledger values. The terminal result is the
            // authoritative snapshot. Totals are assigned directly because extraction
            // can lawfully refine an inventory estimate downward as well as upward.
            + "files_done = $10, "
            + "input_units_done = $11, input_units_total = $12 "
            + "WHERE run_id = $1",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = status });
                cmd.Parameters.Add(new NpgsqlParameter { Value = result.UnitsAttempted });
                cmd.Parameters.Add(new NpgsqlParameter { Value = result.UnitsApplied });
                cmd.Parameters.Add(new NpgsqlParameter { Value = result.UnitsFailed });
                cmd.Parameters.Add(new NpgsqlParameter { Value = result.EntitiesInserted });
                cmd.Parameters.Add(new NpgsqlParameter { Value = result.PhysicalitiesInserted });
                cmd.Parameters.Add(new NpgsqlParameter { Value = result.AttestationsInserted });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = (object?)error ?? DBNull.Value,
                    NpgsqlDbType = NpgsqlDbType.Text,
            });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)result.FilesDone });
                cmd.Parameters.Add(new NpgsqlParameter { Value = result.InputUnitsDone });
                cmd.Parameters.Add(new NpgsqlParameter { Value = result.InputUnitsTotal });
                cmd.Parameters.Add(new NpgsqlParameter
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
    /// Emit the substrate-owned verdict after the terminal journal transition. The
    /// trigger is the authority; this is deliberately a readback rather than a second
    /// throughput calculation. Every ingest entry point uses this observer, so a slow,
    /// unmeasured, or unbaselined run is visible even when no Actions gate follows it.
    /// </summary>
    private void ReportThroughputVerdict(string sourceName)
    {
        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status, throughput_status, throughput_compared, "
                + "throughput_rows, throughput_elapsed_ms, throughput_rows_per_s, "
                + "throughput_baseline_rows_per_s, throughput_slowdown_ratio "
                + "FROM laplace.ingest_run_journal WHERE run_id = $1";
            cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                Console.Error.WriteLine(
                    $"INGEST_THROUGHPUT_READ_FAILED source={sourceName} run={_runId} error=journal-row-missing");
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
            Console.Error.WriteLine(
                $"INGEST_THROUGHPUT_READ_FAILED source={sourceName} run={_runId} "
                + $"error=[{ex.GetType().Name}] {ex.Message}");
        }
    }

    /// <summary>
    /// Per-run insert counts are operational telemetry, not a closure proof: an
    /// entity or physicality may already exist globally and therefore insert zero
    /// rows in this run. The runner's committed source-closure check is authoritative.
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
                "UPDATE laplace.ingest_run_journal SET status = $2, phase = 'failed', "
                + "ended_at = now(), error = $3 "
                + "WHERE run_id = $1",
                cmd =>
                {
                    cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = status });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = error });
                });
            ReleaseLivenessLock();
            return;
        }
        // Failure before OnRunStart (init/inventory) — journal it as its own terminal row
        // so an early crash is still diagnosable. After OnRunFinished, the run is already
        // terminal (e.g. the empty-noop throw) and this is a no-op.
        if (_runId != Guid.Empty) return;
        Execute(
            "INSERT INTO laplace.ingest_run_journal "
            + "(run_id, source_name, source_id, layer, status, phase, ended_at, evidence_persisted, error) "
            + "VALUES ($1, $2, laplace.source_id($2), -1, $3, 'failed', now(), $4, $5)",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid(), NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = sourceName });
                cmd.Parameters.Add(new NpgsqlParameter { Value = status });
                cmd.Parameters.Add(new NpgsqlParameter { Value = _evidencePersisted });
                cmd.Parameters.Add(new NpgsqlParameter { Value = error });
            });
    }

    public void OnRunSkipped(string sourceName, int layerOrder) =>
        Execute(
            "INSERT INTO laplace.ingest_run_journal "
            + "(run_id, source_name, source_id, layer, status, phase, ended_at, evidence_persisted) "
            + "VALUES ($1, $2, laplace.source_id($2), $3, 'skipped-complete', 'complete', now(), $4)",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid(), NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = sourceName });
                cmd.Parameters.Add(new NpgsqlParameter { Value = layerOrder });
                cmd.Parameters.Add(new NpgsqlParameter { Value = _evidencePersisted });
            });

    /// <summary>
    /// Per-file ledger rows (laplace.ingest_file_journal). Calls only enqueue; the file
    /// journal pump bulk-flushes them. A journal failure logs and never aborts the ingest.
    ///
    /// ON CONFLICT DO UPDATE, not DO NOTHING: a file re-opened inside one run (a retry)
    /// is the same unit, and the second attempt is the one that decides its outcome.
    /// Leaving the first row in place would report a retried file by its failed attempt.
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
    /// Advisory counter update for a file still in flight. Never inserts and never
    /// changes status -- the UPDATE is guarded on status = 'running' -- so a progress
    /// event that loses a race with Composed or a terminal status is discarded by
    /// the server rather than resurrecting a stale count onto a finished row.
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
                Console.Error.WriteLine(
                    $"INGEST_FILE_JOURNAL_WRITE_FAILED run={_runId} "
                    + $"error=[{ex.GetType().Name}] {ex.Message}");
            }
            cts.Dispose();
        }
        while (!_fileEvents.IsEmpty)
            FlushFileJournalEvents();
    }

    /// <summary>
    /// Drain file lifecycle events into at most three array-backed statements in one
    /// network round-trip. File workers only enqueue; they never wait on journal I/O.
    /// Events are grouped in lifecycle order because a file has at most one attempt in a
    /// run: started, composed, then terminal. True-skips intentionally omit started.
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
                // The existing pump remains alive while parsing/apply produces no
                // progress events. Use its pooled connection, not a new owner.
                if (heartbeatDue)
                {
                    var heartbeat = new NpgsqlBatchCommand(
                        "UPDATE laplace.ingest_run_journal SET heartbeat_at = clock_timestamp() "
                        + "WHERE run_id = $1 AND status = 'running'");
                    AddParameter(heartbeat, _runId, NpgsqlDbType.Uuid);
                    batch.BatchCommands.Add(heartbeat);
                }

                var started = events.Where(e => e.Kind == FileJournalEventKind.Started).ToArray();
                if (started.Length > 0)
                {
                    var cmd = new NpgsqlBatchCommand(
                        "INSERT INTO laplace.ingest_file_journal AS current_file "
                        + "(run_id, file_label, source_name, status, bytes, modified_at, started_at) "
                        + "SELECT $1, u.file_label, u.source_name, 'running', u.bytes, u.modified_at, u.at "
                        + "FROM unnest($2, $3, $4, $5, $6) "
                        + "AS u(file_label, source_name, bytes, modified_at, at) "
                        + "ON CONFLICT (run_id, file_label) DO UPDATE SET "
                        + "status = 'running', started_at = EXCLUDED.started_at, ended_at = NULL, error = NULL, "
                        + "bytes = EXCLUDED.bytes, "
                        + "modified_at = COALESCE(EXCLUDED.modified_at, current_file.modified_at), "
                        + "file_id = NULL, resume_fingerprint = NULL, records = 0, entities = 0, "
                        + "physicalities = 0, attestations = 0");
                    AddParameter(cmd, _runId, NpgsqlDbType.Uuid);
                    AddParameter(cmd, started.Select(e => e.FileLabel).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, started.Select(e => e.SourceName).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, started.Select(e => e.Bytes).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    AddParameter(cmd, started.Select(e => e.ModifiedAt).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.TimestampTz);
                    AddParameter(cmd, started.Select(e => e.At).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.TimestampTz);
                    batch.BatchCommands.Add(cmd);
                }

                // Coalesced to the LAST event per file: counters are cumulative, so
                // within one flush only the newest sample carries information and
                // sending the rest would be N round-trip rows for one final value.
                var progress = events
                    .Where(e => e.Kind == FileJournalEventKind.Progress)
                    .GroupBy(e => e.FileLabel, StringComparer.Ordinal)
                    .Select(g => g.Last())
                    .ToArray();
                if (progress.Length > 0)
                {
                    // UPDATE, never INSERT: a progress event for a file with no row is
                    // meaningless (Started owns row creation), and status = 'running'
                    // keeps a straggler from overwriting a composed or terminal row.
                    var cmd = new NpgsqlBatchCommand(
                        "UPDATE laplace.ingest_file_journal f SET "
                        + "records = u.records, entities = u.entities, "
                        + "physicalities = u.physicalities, attestations = u.attestations "
                        + "FROM unnest($2, $3, $4, $5, $6) "
                        + "AS u(file_label, records, entities, physicalities, attestations) "
                        + "WHERE f.run_id = $1 AND f.file_label = u.file_label "
                        + "AND f.status = 'running'");
                    AddParameter(cmd, _runId, NpgsqlDbType.Uuid);
                    AddParameter(cmd, progress.Select(e => e.FileLabel).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, progress.Select(e => e.Records).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    AddParameter(cmd, progress.Select(e => e.Entities).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    AddParameter(cmd, progress.Select(e => e.Physicalities).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    AddParameter(cmd, progress.Select(e => e.Attestations).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    batch.BatchCommands.Add(cmd);
                }

                var composed = events.Where(e => e.Kind == FileJournalEventKind.Composed).ToArray();
                if (composed.Length > 0)
                {
                    var cmd = new NpgsqlBatchCommand(
                        "INSERT INTO laplace.ingest_file_journal AS current_file "
                        + "(run_id, file_label, source_name, file_id, resume_fingerprint, status, records, entities, physicalities, attestations, started_at) "
                        + "SELECT $1, u.file_label, u.source_name, u.file_id, u.resume_fingerprint, 'composed', u.records, "
                        + "u.entities, u.physicalities, u.attestations, u.at "
                        + "FROM unnest($2, $3, $4, $5, $6, $7, $8, $9, $10) "
                        + "AS u(file_label, source_name, file_id, resume_fingerprint, records, entities, physicalities, attestations, at) "
                        + "ON CONFLICT (run_id, file_label) DO UPDATE SET "
                        + "file_id = COALESCE(EXCLUDED.file_id, current_file.file_id), "
                        + "resume_fingerprint = COALESCE(EXCLUDED.resume_fingerprint, current_file.resume_fingerprint), "
                        + "status = 'composed', "
                        + "records = EXCLUDED.records, entities = EXCLUDED.entities, "
                        + "physicalities = EXCLUDED.physicalities, attestations = EXCLUDED.attestations");
                    AddParameter(cmd, _runId, NpgsqlDbType.Uuid);
                    AddParameter(cmd, composed.Select(e => e.FileLabel).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, composed.Select(e => e.SourceName).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, composed.Select(e => e.FileId).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bytea);
                    AddParameter(cmd, composed.Select(e => e.ResumeFingerprint).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bytea);
                    AddParameter(cmd, composed.Select(e => e.Records).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    AddParameter(cmd, composed.Select(e => e.Entities).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    AddParameter(cmd, composed.Select(e => e.Physicalities).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    AddParameter(cmd, composed.Select(e => e.Attestations).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    AddParameter(cmd, composed.Select(e => e.At).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.TimestampTz);
                    batch.BatchCommands.Add(cmd);
                }

                var finished = events.Where(e => e.Kind == FileJournalEventKind.Finished).ToArray();
                if (finished.Length > 0)
                {
                    var cmd = new NpgsqlBatchCommand(
                        "INSERT INTO laplace.ingest_file_journal "
                        + "(run_id, file_label, source_name, status, ended_at, error, started_at) "
                        + "SELECT $1, u.file_label, u.source_name, u.status, u.at, u.error, u.at "
                        + "FROM unnest($2, $3, $4, $5, $6) "
                        + "AS u(file_label, source_name, status, at, error) "
                        + "ON CONFLICT (run_id, file_label) DO UPDATE SET "
                        + "status = EXCLUDED.status, ended_at = EXCLUDED.ended_at, error = EXCLUDED.error");
                    AddParameter(cmd, _runId, NpgsqlDbType.Uuid);
                    AddParameter(cmd, finished.Select(e => e.FileLabel).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, finished.Select(e => e.SourceName).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, finished.Select(e => e.Status!).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, finished.Select(e => e.At).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.TimestampTz);
                    AddParameter(cmd, finished.Select(e => e.Error).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    batch.BatchCommands.Add(cmd);
                }

                batch.ExecuteNonQuery();
                if (heartbeatDue) _lastHeartbeatUtc = now;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"INGEST_FILE_JOURNAL_WRITE_FAILED run={_runId} "
                    + $"error=[{ex.GetType().Name}] {ex.Message}");
            }
        }
    }

    private static void AddParameter(NpgsqlBatchCommand command, object value, NpgsqlDbType type) =>
        command.Parameters.Add(new NpgsqlParameter { Value = value, NpgsqlDbType = type });

    private bool Execute(string sql, Action<NpgsqlCommand> bind, string failTag = "INGEST_RUN_JOURNAL_WRITE_FAILED")
    {
        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"{failTag} run={_runId} error=[{ex.GetType().Name}] {ex.Message}");
            return false;
        }
    }
}
