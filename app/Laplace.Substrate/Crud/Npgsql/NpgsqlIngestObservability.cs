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
public sealed partial class NpgsqlIngestObservability : IIngestObservability
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
    private readonly object _livenessConnGate = new();
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
        // A caller that has the witness id records that id; otherwise the source is the id of
        // its canonical key (SubstrateCanonicalIds.Source), a different entity from the
        // [authority, release] witness attestations are filed under.
        bool journaled = Execute(
            unidentified ? "journal.run_start_unidentified" : "journal.run_start_identified",
            cmd =>
            {
                cmd.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Add(new NpgsqlParameter { Value = sourceName });
                if (!unidentified)
                    cmd.Add(new NpgsqlParameter { Value = sourceId ?? SubstrateCanonicalIds.Source(sourceName).ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
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
    /// cannot reclaim it. Subsequent journal and file-pump commands reuse that session under
    /// <see cref="_livenessConnGate"/> so observability occupies at most one pooled slot at
    /// a time (preserving a control slot on a two-connection pool). Failure to acquire is
    /// logged and the ingest proceeds, but without the lock the run reads as orphaned once
    /// its heartbeat is stale.
    /// </summary>
    private void AcquireLivenessLock()
    {
        lock (_livenessConnGate)
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
        lock (_livenessConnGate)
        {
            _livenessConn?.Dispose();   // session end releases the advisory lock
            _livenessConn = null;
        }
    }

    private void WithJournalConnection(Action<NpgsqlConnection> action)
    {
        lock (_livenessConnGate)
        {
            if (_livenessConn is { FullState: System.Data.ConnectionState.Open } pinned)
            {
                action(pinned);
                return;
            }
        }
        using var conn = _ds.OpenConnection();
        action(conn);
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
}
