using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Ingestion;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Persists the ingest run ledger (laplace.ingest_run_journal): one row per run,
/// 'running' at start, driven to a terminal status on every exit path. Writes are
/// small, synchronous, and blocking — a run-status row is the whole point, so it is
/// never fire-and-forget; but journaling is ops metadata, so a journal write failure
/// logs loudly and never aborts the ingest itself. One runner drives one run at a
/// time (the ingest mutex), so a single current-run id suffices.
/// </summary>
public sealed class NpgsqlIngestObservability : IIngestObservability
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

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

    private Guid _runId;
    private bool _active;
    private DateTime _lastProgressUtc;
    private NpgsqlConnection? _livenessConn;

    public NpgsqlIngestObservability(NpgsqlDataSource dataSource, bool evidencePersisted = true)
    {
        _ds = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _evidencePersisted = evidencePersisted;
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

        // The file rows of a reconciled run. A file left 'running' by a kill is the one
        // the run was mid-apply on — the single most useful fact in the ledger after a
        // crash — so it is driven to the same terminal state as its run rather than left
        // ambiguous. Keyed off the run's status, which the UPDATE above has just settled.
        Execute(
            "UPDATE laplace.ingest_file_journal f SET status = 'cancelled', ended_at = now(), "
            + "error = 'file did not reach completion: run cancelled (cluster restart, OOM kill, "
            + "or terminated session). Reconciled at the start of the next run.' "
            + "WHERE f.status = 'running' "
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

    public void OnRunFinished(string sourceName, IngestRunResult result, string status, string? error = null)
    {
        if (!_active) return;
        _active = false;
        // error is written HERE, not by a follow-up OnRunFailed: that method returns early
        // once the run is terminal, so every failure reaching this path landed in the ledger
        // as status=failed with error NULL. MEASURED 2026-08-10: the document lane recorded
        // failed at files_done=199/207 twice, with no diagnostic in the row either time.
        Execute(
            "UPDATE laplace.ingest_run_journal SET "
            + "status = $2, ended_at = now(), "
            + "units_attempted = $3, units_applied = $4, units_failed = $5, "
            + "entities = $6, physicalities = $7, attestations = $8, "
            + "error = COALESCE($9, error), "
            // files_done rode ONLY the periodic progress UPDATE, so a run whose last flush
            // did not land ended with a ledger count below the one its own status was
            // derived from. Terminal write now carries the run's final count.
            + "files_done = GREATEST(files_done, $10) "
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

        // The other direction is equally a fault, and was previously not reported at
        // all. An entity IS composed content: every constituent codepoint carries a
        // coordinate, so 'C', 'A' and 'T' each have one and therefore 'CAT' has one.
        // No class of entity legitimately lacks a placement — an entity with none was
        // minted from a hashed string rather than composed from the DAG (GH #1038,
        // measured 2026-08-12: 10,293 such rows, all reporting zero constituents).
        //
        // The one benign reading is timing, not design: entities COPY before
        // physicalities within an apply, so a run interrupted between the two phases
        // ends with a real entity surplus and nothing wrong. The message names that
        // explicitly instead of asserting a defect the counts cannot distinguish.
        Console.Error.WriteLine(
            $"INGEST_ENTITY_SURPLUS source={sourceName} run={_runId} "
            + $"entities={result.EntitiesInserted} physicalities={result.PhysicalitiesInserted} "
            + $"surplus={-delta} — this run declared {-delta} entities it never placed. "
            + "Benign if the run was interrupted between the entity and physicality COPY "
            + "phases; otherwise these entities were minted outside the compose DAG and "
            + "have no coordinate. See GH #1038.");
    }

    public void OnRunFailed(string sourceName, string status, string error)
    {
        if (_active)
        {
            _active = false;
            Execute(
                "UPDATE laplace.ingest_run_journal SET status = $2, ended_at = now(), error = $3 "
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
            + "(run_id, source_name, source_id, layer, status, ended_at, evidence_persisted, error) "
            + "VALUES ($1, $2, laplace.source_id($2), -1, $3, now(), $4, $5)",
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
            + "(run_id, source_name, source_id, layer, status, ended_at, evidence_persisted) "
            + "VALUES ($1, $2, laplace.source_id($2), $3, 'skipped-complete', now(), $4)",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid(), NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = sourceName });
                cmd.Parameters.Add(new NpgsqlParameter { Value = layerOrder });
                cmd.Parameters.Add(new NpgsqlParameter { Value = _evidencePersisted });
            });

    /// <summary>
    /// Per-file ledger rows (laplace.ingest_file_journal). Same law as every other write
    /// in this class: a journal failure logs and never aborts the ingest.
    ///
    /// ON CONFLICT DO UPDATE, not DO NOTHING: a file re-opened inside one run (a retry)
    /// is the same unit, and the second attempt is the one that decides its outcome.
    /// Leaving the first row in place would report a retried file by its failed attempt.
    /// </summary>
    public void OnFileStarted(string sourceName, string fileLabel, long bytes = 0)
    {
        if (!_active) return;
        Execute(
            "INSERT INTO laplace.ingest_file_journal "
            + "(run_id, file_label, source_name, status, bytes) VALUES ($1, $2, $3, 'running', $4) "
            + "ON CONFLICT (run_id, file_label) DO UPDATE SET "
            + "status = 'running', started_at = now(), ended_at = NULL, error = NULL, "
            + "bytes = EXCLUDED.bytes",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = fileLabel });
                cmd.Parameters.Add(new NpgsqlParameter { Value = sourceName });
                cmd.Parameters.Add(new NpgsqlParameter { Value = bytes });
            },
            "INGEST_FILE_JOURNAL_WRITE_FAILED");
    }

    public void OnFileFinished(
        string sourceName, string fileLabel, string status,
        long records = 0, long entities = 0, long physicalities = 0, long attestations = 0,
        string? error = null)
    {
        if (!_active) return;
        // Upsert rather than UPDATE: a true-skipped file never opens, so it has no
        // 'running' row to update, and skipped-complete is exactly the state worth
        // recording — it is how a resumed run accounts for the prefix it did not redo.
        Execute(
            "INSERT INTO laplace.ingest_file_journal "
            + "(run_id, file_label, source_name, status, ended_at, records, entities, physicalities, attestations, error) "
            + "VALUES ($1, $2, $3, $4, now(), $5, $6, $7, $8, $9) "
            + "ON CONFLICT (run_id, file_label) DO UPDATE SET "
            + "status = EXCLUDED.status, ended_at = EXCLUDED.ended_at, records = EXCLUDED.records, "
            + "entities = EXCLUDED.entities, physicalities = EXCLUDED.physicalities, "
            + "attestations = EXCLUDED.attestations, error = EXCLUDED.error",
            cmd =>
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = _runId, NpgsqlDbType = NpgsqlDbType.Uuid });
                cmd.Parameters.Add(new NpgsqlParameter { Value = fileLabel });
                cmd.Parameters.Add(new NpgsqlParameter { Value = sourceName });
                cmd.Parameters.Add(new NpgsqlParameter { Value = status });
                cmd.Parameters.Add(new NpgsqlParameter { Value = records });
                cmd.Parameters.Add(new NpgsqlParameter { Value = entities });
                cmd.Parameters.Add(new NpgsqlParameter { Value = physicalities });
                cmd.Parameters.Add(new NpgsqlParameter { Value = attestations });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    Value = (object?)error ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text
                });
            },
            "INGEST_FILE_JOURNAL_WRITE_FAILED");
    }

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
