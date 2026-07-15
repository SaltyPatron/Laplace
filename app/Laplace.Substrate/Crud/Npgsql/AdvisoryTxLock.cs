using global::Npgsql;
using Microsoft.Extensions.Logging;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Transaction-scoped advisory locks with bounded, observable waits. The old
/// form — SELECT pg_advisory_xact_lock(...) inside a CommandTimeout=0 command —
/// waited forever with zero output whenever another backend held the lock
/// (typically a stale backend from a killed run); every such wait presented as
/// "ingest hung at the end" with nothing to act on. Acquisition now runs under
/// a lock_timeout window; each window that expires logs the holding backend
/// (pid, state, age, query) and retries, so a wedged run names the backend to
/// kill instead of hanging silently forever.
/// </summary>
internal static class AdvisoryTxLock
{
    private const string LockTimeoutWindow = "30s";
    private const int WindowSeconds = 30;

    /// <summary>
    /// Begin a transaction on <paramref name="conn"/>, apply
    /// <paramref name="setLocalGucs"/> (a trusted compile-time constant, ';'-terminated),
    /// and take the named xact advisory lock. On lock_timeout the aborted
    /// transaction is rolled back, the holder is logged, and acquisition
    /// retries on a fresh transaction. Returns the transaction holding the lock.
    /// </summary>
    internal static async Task<NpgsqlTransaction> BeginWithLockAsync(
        NpgsqlConnection conn, string lockName, string setLocalGucs, ILogger log, CancellationToken ct)
    {
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
                    // Parameterized: one statement text for every lock name, so the
                    // auto-prepare cache holds a single plan instead of one per name.
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
