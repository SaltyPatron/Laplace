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
    /// classid of the MEASUREMENT LANE — mutual exclusion between "something is writing
    /// to the substrate" and "something is timing it". Ingest holds it SHARED for a run's
    /// lifetime; a measurement holds it EXCLUSIVE for its duration. Postgres conflicts the
    /// two modes, so ingests never serialise against each other (the same refusal
    /// wait-for-quiet-substrate.sh's header records) while a measurement still empties
    /// the substrate.
    ///
    /// <para>MEASURED 2026-08-15: generation.compose_batch returned 264,605 / 244,533 /
    /// 81,701 / 316,998 / 144,256 / 144,947 / 153,203 / 301,571 ms across one session for
    /// near-identical code, with laplace.ingest_run_journal showing an active run
    /// throughout — a 3.9x spread, and causal claims were drawn from single runs inside
    /// it. Re-measured quiesced, realize.resolve_name read 36,000 -> 0 ms and
    /// generation.separator_ids 9,450 -> 11 ms with NO code change: both had been
    /// recorded as defects and both were contention. The cost is not the wasted session,
    /// it is the code written against those numbers.</para>
    ///
    /// <para>Its own class, not the LPLK liveness beacon's: that space is keyed
    /// hashtext(run_id::text), an arbitrary int32 that could equal this fixed key.</para>
    ///
    /// <para>Lives here because this file is the one sanctioned home for a database
    /// advisory lock (IngestMutexGateTests). One definition, so a shell copy and a C#
    /// copy cannot drift; every caller goes through <see cref="HoldMeasurementLaneAsync"/>.</para>
    /// </summary>
    internal const int MeasurementLaneLockClass = 0x4C504C4E; // "LPLN"

    /// <summary>objid of the measurement lane. One lane, so one key.</summary>
    internal const int MeasurementLaneLockKey = 0;

    /// <summary>
    /// Take the measurement lane on <paramref name="conn"/> and hold it for the life of
    /// that connection — a SESSION lock, so the server drops it the instant the holder
    /// dies and no cleanup path has to run. Transaction scope would be wrong in both
    /// directions: an ingest holds this across thousands of transactions, and a
    /// measurement holds it across a subprocess that runs none.
    ///
    /// <para><paramref name="exclusive"/> false is the ingest side (any number coexist);
    /// true is the measurement side (excludes every writer). Waits in bounded windows,
    /// reporting the holders each window rather than hanging silently — the failure this
    /// file's own history records as "ingest hung at the end" with nothing to act on.</para>
    /// </summary>
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

    /// <summary>
    /// Name every backend holding the lane, with mode and query. Asked of pg_locks, which
    /// is the authority that granted them: a waiter that reports only "busy" leaves an
    /// operator with nothing to act on, and the lane is deliberately capable of being held
    /// for the length of a whole measurement.
    /// </summary>
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
