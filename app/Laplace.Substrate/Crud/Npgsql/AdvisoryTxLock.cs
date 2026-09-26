using global::Npgsql;
using Microsoft.Extensions.Logging;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Transaction-scoped advisory locks with bounded, observable waits for operations that
/// genuinely require named mutual exclusion. Content-addressed ingest apply is not one of
/// those operations: it owns concurrency through bulk presence verification, primary-key
/// identity and re-probe/retry of concurrent landing races.
/// </summary>
internal static class AdvisoryTxLock
{
    private const string LockTimeoutWindow = "30s";
    private const int WindowSeconds = 30;

    internal const int MeasurementLaneLockClass = 0x4C504C4E; // "LPLN"
    internal const int MeasurementLaneLockKey = 0;

    // A staged load reads current leaves and standing and swaps rebuilt leaves in; two
    // loads interleaved would each rebuild from the rows the other replaces.
    internal const int StagedLoadLockClass = 0x4C504C44; // "LPLD"
    internal const int StagedLoadLockKey = 0;

    internal static async Task HoldMeasurementLaneAsync(
        NpgsqlConnection conn, bool exclusive, Action<string>? onWaiting, CancellationToken ct)
    {
        var fn = exclusive ? "pg_advisory_lock" : "pg_advisory_lock_shared";
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using (var win = conn.CreateCommand())
                {
                    win.CommandText = $"SET lock_timeout = '{LockTimeoutWindow}'";
                    await win.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var take = conn.CreateCommand())
                {
                    take.CommandTimeout = 0;
                    take.CommandText = $"SELECT {fn}($1, $2)";
                    take.Parameters.AddWithValue(MeasurementLaneLockClass);
                    take.Parameters.AddWithValue(MeasurementLaneLockKey);
                    await take.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var clear = conn.CreateCommand())
                {
                    clear.CommandText = "SET lock_timeout = 0";
                    await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                onWaiting?.Invoke(await DescribeLaneHoldersAsync(conn, attempt, ct).ConfigureAwait(false));
            }
        }
    }

    private static async Task<string> DescribeLaneHoldersAsync(
        NpgsqlConnection conn, int attempt, CancellationToken ct)
    {
        var head = $"measurement lane still held after ~{WindowSeconds * attempt}s (attempt {attempt})";
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT l.pid, l.mode, coalesce(a.state,'?'), left(coalesce(a.query,''),120) "
                + "FROM pg_locks l LEFT JOIN pg_stat_activity a USING (pid) "
                + "WHERE l.locktype = 'advisory' AND l.granted "
                + "  AND l.database = (SELECT d.oid FROM pg_database d WHERE d.datname = current_database()) "
                + $"  AND l.classid = {MeasurementLaneLockClass}::oid "
                + $"  AND l.objid = {MeasurementLaneLockKey}::oid AND l.objsubid = 2 "
                + "ORDER BY l.pid";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var parts = new List<string>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                parts.Add($"pid {r.GetInt32(0)} {r.GetString(1)} ({r.GetString(2)}): {r.GetString(3)}");
            return parts.Count == 0 ? $"{head} — no holder visible; retrying" : $"{head} — {string.Join("; ", parts)}";
        }
        catch (Exception ex)
        {
            return $"{head} — holder diagnostics failed ({ex.Message}); retrying";
        }
    }

    /// <summary>
    /// Begin a transaction, apply trusted transaction-local GUCs, and when requested take
    /// a named advisory lock. `laplace_apply_batch` is deliberately lock-free: the writer
    /// already performs an in-transaction set presence verification immediately before
    /// COPY, and concurrent content-addressed races are re-probed through the ingest retry
    /// policy. A global mutex here made every apply process-wide serial and defeated the
    /// parallel decomposition/COPY/fold architecture.
    /// </summary>
    internal static async Task<NpgsqlTransaction> BeginWithLockAsync(
        NpgsqlConnection conn, string lockName, string setLocalGucs, ILogger log, CancellationToken ct)
    {
        if (string.Equals(lockName, "laplace_apply_batch", StringComparison.Ordinal))
        {
            var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using var guc = conn.CreateCommand();
                guc.Transaction = tx;
                guc.CommandText = $"{setLocalGucs}SET LOCAL lock_timeout = 0";
                await guc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return tx;
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                await tx.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        for (int attempt = 1; ; attempt++)
        {
            var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var guc = conn.CreateCommand())
                {
                    guc.Transaction = tx;
                    guc.CommandText = $"{setLocalGucs}SET LOCAL lock_timeout = '{LockTimeoutWindow}'";
                    await guc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var take = conn.CreateCommand())
                {
                    take.Transaction = tx;
                    take.CommandTimeout = 0;
                    take.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))";
                    take.Parameters.AddWithValue(lockName);
                    await take.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var clear = conn.CreateCommand())
                {
                    clear.Transaction = tx;
                    clear.CommandText = "SET LOCAL lock_timeout = 0";
                    await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return tx;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                await tx.DisposeAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                await LogHolderAsync(conn, lockName, attempt, log, ct).ConfigureAwait(false);
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                await tx.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task LogHolderAsync(
        NpgsqlConnection conn, string lockName, int attempt, ILogger log, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "WITH k AS (SELECT hashtextextended($1, 0) AS key) "
                + "SELECT a.pid, coalesce(a.state, '?'), coalesce(now() - a.query_start, interval '0'), "
                + "       left(coalesce(a.query, ''), 200) "
                + "FROM pg_locks l "
                + "JOIN k ON l.locktype = 'advisory' AND l.granted AND l.objsubid = 1 "
                + "      AND l.classid = ((k.key >> 32) & 4294967295)::oid "
                + "      AND l.objid   = (k.key & 4294967295)::oid "
                + "JOIN pg_stat_activity a USING (pid) "
                + "WHERE a.pid <> pg_backend_pid()";
            cmd.Parameters.AddWithValue(lockName);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            bool any = false;
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                any = true;
                int pid = r.GetInt32(0);
                log.LogWarning(
                    "advisory lock '{Lock}' still held after ~{Sec}s (attempt {Attempt}) by pid {Pid} "
                    + "({State}, query running {Age}): {Query} — if that backend belongs to a dead run, "
                    + "SELECT pg_terminate_backend({Pid}) frees this ingest",
                    lockName, WindowSeconds * attempt, attempt, pid,
                    r.GetString(1), r.GetFieldValue<TimeSpan>(2), r.GetString(3), pid);
            }
            if (!any)
                log.LogWarning(
                    "advisory lock '{Lock}' wait timed out (attempt {Attempt}) but no holder is visible — retrying",
                    lockName, attempt);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "advisory lock '{Lock}' holder diagnostics failed — retrying acquisition", lockName);
        }
    }
}
