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

    private readonly NpgsqlDataSource _ds;
    private readonly bool _evidencePersisted;

    private Guid _runId;
    private bool _active;
    private DateTime _lastProgressUtc;

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

    public void OnRunFinished(string sourceName, IngestRunResult result, string status)
    {
        if (!_active) return;
        _active = false;
        Execute(
            "UPDATE laplace.ingest_run_journal SET "
            + "status = $2, ended_at = now(), "
            + "units_attempted = $3, units_applied = $4, units_failed = $5, "
            + "entities = $6, physicalities = $7, attestations = $8 "
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
            });
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

    private void Execute(string sql, Action<NpgsqlCommand> bind)
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
                $"INGEST_RUN_JOURNAL_WRITE_FAILED run={_runId} error=[{ex.GetType().Name}] {ex.Message}");
        }
    }
}
