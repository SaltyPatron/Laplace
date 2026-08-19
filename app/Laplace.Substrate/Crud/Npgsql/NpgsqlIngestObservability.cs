using global::Npgsql;
using NpgsqlTypes;
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
    /// The lock IS the liveness proof: the server releases it the moment this
    /// process's session dies — OOM kill, SIGKILL, cluster bounce, cancelled CI
    /// runner — so unlike a journal column it cannot be left behind by a process
    /// that never got to clean up. Readers (ReconcileOrphanedRuns here, and
    /// scripts/wait-for-quiet-substrate.sh) treat a 'running' row without this
    /// lock as a corpse. The value is arbitrary but load-bearing: the gate script
    /// carries the same constant, and they must agree.
    /// </summary>
    private const int RunLivenessLockClass = 0x4C504C4B; // "LPLK"

    private readonly NpgsqlDataSource _ds;
    private readonly bool _evidencePersisted;
    private readonly int _fileJournalFlushRows;

    private Guid _runId;
    private volatile bool _active;
    private DateTime _lastProgressUtc;
    private NpgsqlConnection? _livenessConn;
    private readonly System.Collections.Concurrent.ConcurrentQueue<FileJournalEvent> _fileEvents = new();
    private readonly object _fileFlushGate = new();
    private CancellationTokenSource? _filePumpCts;
    private Task? _filePump;

    private enum FileJournalEventKind : byte { Started, Composed, Finished }

    private readonly record struct FileJournalEvent(
        FileJournalEventKind Kind,
        string SourceName,
        string FileLabel,
        DateTimeOffset At,
        long Bytes = 0,
        byte[]? FileId = null,
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

    public void OnRunStart(string sourceName, int layerOrder, IngestInventory? inventory)
    {
        _runId = Guid.NewGuid();
        _active = true;
        _lastProgressUtc = DateTime.MinValue;
        ReconcileOrphanedRuns();
        Execute(
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
        AcquireLivenessLock();
        StartFileJournalPump();
    }

    /// <summary>
    /// Hold the per-run session advisory lock for the run's lifetime, on a
    /// dedicated connection pinned outside the pool (a pooled connection would be
    /// pruned while the run composes in memory between COPY bursts, releasing the
    /// lock under a live run). Failure to acquire logs loudly and never aborts
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
    /// Terminate journal rows whose backend no longer exists.
    ///
    /// A run that dies WITH the cluster never reaches OnRunFinished, so its row keeps
    /// status='running' forever and every later reader — verify-ingest-journal.sh,
    /// ensure-foundation.sh's layer check, source_status — believes an ingest is still
    /// in flight. Nothing else reconciles it: the process that would have written the
    /// terminal row is the process that died.
    ///
    /// MEASURED 2026-08-10: FrameNetDecomposer sat at 13863/14900 files, status
    /// 'running', after `apt install bc` triggered needrestart -> `systemctl restart
    /// laplace-postgresql.service` and terminated the backend 93% through the corpus.
    /// The row still read 'running' with no process behind it.
    ///
    /// Reconciled at the START of the next run AND by the deploy gate
    /// (scripts/wait-for-quiet-substrate.sh), both against the same authority: the
    /// per-run session advisory lock. The previous criterion here asked
    /// pg_stat_activity whether ANY backend predated the run — and any long-lived
    /// client defeats it: the API endpoint's connection pool predates every run,
    /// so the NOT EXISTS never held and reconciliation NEVER FIRED. MEASURED
    /// 2026-08-13: two UDDecomposer corpses sat 'running' for 8 and 6 hours with
    /// zero backends behind them, wedging every deploy behind the substrate lock.
    /// A lock keyed to the run and dropped by the server on session death is
    /// per-run, unforgeable, and cannot be left behind.
    /// </summary>
    private void ReconcileOrphanedRuns()
    {
        Execute(
            // 'cancelled', not 'interrupted': ingest_run_journal.status carries a CHECK
            // constraint and 'interrupted' is not a member. The UPDATE would raise 23514,
            // Execute() swallows it, and the row stays 'running' — the exact failure this
            // reconciliation exists to repair.
            "UPDATE laplace.ingest_run_journal j SET status = 'cancelled', ended_at = now(), "
            + "error = 'run did not reach completion: liveness lock absent (cluster restart, "
            + "OOM kill, or terminated session). Reconciled at the start of the next run.' "
            + "WHERE j.status = 'running' "
            + "  AND NOT EXISTS (SELECT 1 FROM pg_locks l "
            + "                   WHERE l.locktype = 'advisory' "
            + "                     AND l.database = (SELECT d.oid FROM pg_database d "
            + "                                        WHERE d.datname = current_database()) "
            + $"                    AND l.classid = {RunLivenessLockClass}::oid "
            + "                     AND l.objsubid = 2 "
            + "                     AND l.objid::bigint = (hashtext(j.run_id::text)::bigint & 4294967295))",
            static _ => { });

        // The file rows of a reconciled run. A file left 'running' was still producing;
        // one left 'composed' was queued at the apply boundary. Both are incomplete and
        // must follow the run to a terminal state rather than remaining falsely active.
        // Keyed off the run's status, which the UPDATE above has just settled.
        Execute(
            "UPDATE laplace.ingest_file_journal f SET status = 'cancelled', ended_at = now(), "
            + "error = 'file did not reach completion: run cancelled (cluster restart, OOM kill, "
            + "or terminated session). Reconciled at the start of the next run.' "
            + "WHERE f.status IN ('running','composed') "
            + "  AND EXISTS (SELECT 1 FROM laplace.ingest_run_journal j "
            + "               WHERE j.run_id = f.run_id AND j.status <> 'running')",
            static _ => { }, "INGEST_FILE_JOURNAL_WRITE_FAILED");
    }

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
            + "files_done = $8, input_units_done = $9 "
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
        string sourceName, TimeSpan foldDrain, TimeSpan writerMaintenance)
    {
        if (!_active) return;
        Execute(
            "UPDATE laplace.ingest_run_journal SET fold_drain_ms = $2, writer_maintenance_ms = $3 "
            + "WHERE run_id = $1",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)foldDrain.TotalMilliseconds });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (long)writerMaintenance.TotalMilliseconds });
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
            });

        WarnIfPlacementsExceedEntities(sourceName, result);
        ReleaseLivenessLock();
    }

    /// <summary>
    /// EVERY PLACEMENT NEEDS AN ENTITY, and this row already held the proof.
    ///
    /// physicalities.entity_id has no foreign key — only NOT NULL and an
    /// octet_length CHECK — so a run may write a placement for an entity it never
    /// declares and nothing objects. The two counts sat in this very UPDATE, one
    /// column apart, and were never compared.
    ///
    /// MEASURED 2026-08-12: 899,179 physicalities in the substrate referencing an
    /// entity id with no entity row, all type=Content, all composed (avg 30
    /// constituents), and participating in ZERO attestations — inert placements
    /// pointing at nothing. The journal had recorded the split per run all along:
    /// SemLinkDecomposer 7,060 entities against 11,626 physicalities (+4,566),
    /// OMWDecomposer 5,111,969 against 6,006,582 (+894,613). Those two sum to
    /// exactly 899,179. FrameNet, MapNet and WordFrameNet came out balanced.
    ///
    /// Warn rather than throw: the run's rows are already committed by the time
    /// this executes, so failing here would report a completed ingest as failed
    /// and tell the operator nothing they can act on. The point is that the next
    /// occurrence is loud on the first run instead of found by hand-joining two
    /// tables months later. GH #1027.
    ///
    /// A surplus of ENTITIES is reported too (INGEST_ENTITY_SURPLUS), with the
    /// one benign reading named in the message: entities COPY before
    /// physicalities, so a run interrupted between the two phases legitimately
    /// shows one. Any other cause is entities minted outside the compose DAG
    /// with no coordinate. GH #1038.
    /// </summary>
    private void WarnIfPlacementsExceedEntities(string sourceName, IngestRunResult result)
    {
        long delta = result.PhysicalitiesInserted - result.EntitiesInserted;
        if (delta == 0) return;

        // Console.Error, matching INGEST_RUN_JOURNAL_WRITE_FAILED above: this class
        // takes no logger, and ingest logs are scraped for these tokens.
        if (delta > 0)
        {
            Console.Error.WriteLine(
                $"INGEST_PLACEMENT_SURPLUS source={sourceName} run={_runId} "
                + $"entities={result.EntitiesInserted} physicalities={result.PhysicalitiesInserted} "
                + $"surplus={delta} — this run inserted {delta} more placement(s) than entities. "
                + "That can be legitimate (placements added to entities declared by an earlier "
                + "run), but physicalities.entity_id has no FK, so any placement whose entity was "
                + "never declared is dangling and invisible to the database. See GH #1027.");
            return;
        }

        long entitySurplus = -delta;
        long unexplained = Math.Max(
            0, entitySurplus - result.GovernedIdentitiesWithoutPhysicality);
        if (unexplained == 0)
        {
            Console.Error.WriteLine(
                $"INGEST_GOVERNED_IDENTITY_DELTA source={sourceName} run={_runId} "
                + $"entities={result.EntitiesInserted} physicalities={result.PhysicalitiesInserted} "
                + $"delta={entitySurplus} governed_nonphysical={result.GovernedIdentitiesWithoutPhysicality} "
                + "unexplained=0 — non-content identities were admitted by explicit entity type; "
                + "content/composition placement was validated separately.");
            return;
        }

        Console.Error.WriteLine(
            $"INGEST_ENTITY_SURPLUS source={sourceName} run={_runId} "
            + $"entities={result.EntitiesInserted} physicalities={result.PhysicalitiesInserted} "
            + $"surplus={entitySurplus} governed_nonphysical={result.GovernedIdentitiesWithoutPhysicality} "
            + $"unexplained={unexplained} — the run-level admission gate should have rejected "
            + "unplaced content; inspect interrupted COPY state or an unclassified identity type. "
            + "See GH #1038.");
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
    public void OnFileStarted(string sourceName, string fileLabel, long bytes = 0)
    {
        if (!_active) return;
        _fileEvents.Enqueue(new FileJournalEvent(
            FileJournalEventKind.Started, sourceName, fileLabel, DateTimeOffset.UtcNow, Bytes: bytes));
    }

    public void OnFileFinished(
        string sourceName, string fileLabel, string status, string? error = null)
    {
        if (!_active) return;
        _fileEvents.Enqueue(new FileJournalEvent(
            FileJournalEventKind.Finished, sourceName, fileLabel, DateTimeOffset.UtcNow,
            Status: status, Error: error));
    }

    public void OnFileComposed(
        string sourceName, string fileLabel, Hash128? fileId = null,
        long records = 0, long entities = 0, long physicalities = 0, long attestations = 0)
    {
        if (!_active) return;
        _fileEvents.Enqueue(new FileJournalEvent(
            FileJournalEventKind.Composed, sourceName, fileLabel, DateTimeOffset.UtcNow,
            FileId: fileId?.ToBytes(), Records: records, Entities: entities,
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
            if (events.Count == 0) return;

            try
            {
                using var conn = _ds.OpenConnection();
                using var batch = new NpgsqlBatch(conn);

                var started = events.Where(e => e.Kind == FileJournalEventKind.Started).ToArray();
                if (started.Length > 0)
                {
                    var cmd = new NpgsqlBatchCommand(
                        "INSERT INTO laplace.ingest_file_journal AS current_file "
                        + "(run_id, file_label, source_name, status, bytes, started_at) "
                        + "SELECT $1, u.file_label, u.source_name, 'running', u.bytes, u.at "
                        + "FROM unnest($2, $3, $4, $5) AS u(file_label, source_name, bytes, at) "
                        + "ON CONFLICT (run_id, file_label) DO UPDATE SET "
                        + "status = 'running', started_at = EXCLUDED.started_at, ended_at = NULL, error = NULL, "
                        + "bytes = EXCLUDED.bytes, file_id = NULL, records = 0, entities = 0, "
                        + "physicalities = 0, attestations = 0");
                    AddParameter(cmd, _runId, NpgsqlDbType.Uuid);
                    AddParameter(cmd, started.Select(e => e.FileLabel).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, started.Select(e => e.SourceName).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, started.Select(e => e.Bytes).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bigint);
                    AddParameter(cmd, started.Select(e => e.At).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.TimestampTz);
                    batch.BatchCommands.Add(cmd);
                }

                var composed = events.Where(e => e.Kind == FileJournalEventKind.Composed).ToArray();
                if (composed.Length > 0)
                {
                    var cmd = new NpgsqlBatchCommand(
                        "INSERT INTO laplace.ingest_file_journal AS current_file "
                        + "(run_id, file_label, source_name, file_id, status, records, entities, physicalities, attestations, started_at) "
                        + "SELECT $1, u.file_label, u.source_name, u.file_id, 'composed', u.records, "
                        + "u.entities, u.physicalities, u.attestations, u.at "
                        + "FROM unnest($2, $3, $4, $5, $6, $7, $8, $9) "
                        + "AS u(file_label, source_name, file_id, records, entities, physicalities, attestations, at) "
                        + "ON CONFLICT (run_id, file_label) DO UPDATE SET "
                        + "file_id = COALESCE(EXCLUDED.file_id, current_file.file_id), "
                        + "status = 'composed', "
                        + "records = EXCLUDED.records, entities = EXCLUDED.entities, "
                        + "physicalities = EXCLUDED.physicalities, attestations = EXCLUDED.attestations");
                    AddParameter(cmd, _runId, NpgsqlDbType.Uuid);
                    AddParameter(cmd, composed.Select(e => e.FileLabel).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, composed.Select(e => e.SourceName).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                    AddParameter(cmd, composed.Select(e => e.FileId).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Bytea);
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

    private void Execute(string sql, Action<NpgsqlCommand> bind, string failTag = "INGEST_RUN_JOURNAL_WRITE_FAILED")
    {
        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"{failTag} run={_runId} error=[{ex.GetType().Name}] {ex.Message}");
        }
    }
}
