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
        var operation = exclusive ? "advisory.lane_exclusive" : "advisory.lane_shared";
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using (var win = NpgsqlCatalog.Command(conn, null, "advisory.timeout_session", LockTimeoutWindow))
                {
                    await win.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var take = NpgsqlCatalog.Command(conn, null, operation,
                    MeasurementLaneLockClass, MeasurementLaneLockKey))
                {
                    take.CommandTimeout = 0;
                    await take.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var clear = NpgsqlCatalog.Command(conn, null, "advisory.timeout_session", "0"))
                {
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
            await using var cmd = NpgsqlCatalog.Command(conn, null, "advisory.lane_holders",
                MeasurementLaneLockClass, MeasurementLaneLockKey);
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
    /// a named advisory lock. <c>laplace_apply_batch</c> takes no lock: the writer verifies
    /// set presence inside the transaction immediately before COPY, and concurrent landings
    /// of the same content-addressed id are re-probed through the ingest retry policy, so
    /// working sets persist in parallel.
    /// </summary>
    internal static async Task<NpgsqlTransaction> BeginWithLockAsync(
        NpgsqlConnection conn, string lockName, PostgresWriteDurability durability, ILogger log, CancellationToken ct,
        Func<NpgsqlTransaction, string, CancellationToken, Task>? configure = null)
    {
        string commitMode = durability switch
        {
            PostgresWriteDurability.Asynchronous => "off",
            PostgresWriteDurability.Synchronous => "on",
            _ => throw new ArgumentOutOfRangeException(nameof(durability)),
        };
        async Task ConfigureAsync(NpgsqlTransaction transaction, string timeout)
        {
            if (configure is not null)
            {
                await configure(transaction, timeout, ct).ConfigureAwait(false);
                return;
            }
            await using var command = NpgsqlCatalog.Command(conn, transaction, "write.begin_settings", commitMode, timeout);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (string.Equals(lockName, "laplace_apply_batch", StringComparison.Ordinal))
        {
            var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await ConfigureAsync(tx, "0").ConfigureAwait(false);
                return tx;
            }
            catch
            {
                await RollbackAndDisposeAsync(tx, lockName, log).ConfigureAwait(false);
                throw;
            }
        }

        for (int attempt = 1; ; attempt++)
        {
            var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await ConfigureAsync(tx, LockTimeoutWindow).ConfigureAwait(false);
                await using (var take = NpgsqlCatalog.Command(conn, tx, "advisory.named_transaction", lockName))
                {
                    take.CommandTimeout = 0;
                    await take.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var clear = NpgsqlCatalog.Command(conn, tx, "advisory.timeout_local", "0"))
                {
                    await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return tx;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                await RollbackAndDisposeAsync(tx, lockName, log).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                await LogHolderAsync(conn, lockName, attempt, log, ct).ConfigureAwait(false);
            }
            catch
            {
                await RollbackAndDisposeAsync(tx, lockName, log).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task RollbackAndDisposeAsync(NpgsqlTransaction transaction, string lockName, ILogger log)
    {
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception failure)
        {
            log.LogWarning(failure, "Substrate transaction rollback failed for {LockName}; retaining the primary failure.", lockName);
        }
        try { await transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception failure)
        {
            log.LogWarning(failure, "Substrate transaction disposal failed for {LockName}; retaining the primary failure.", lockName);
        }
    }

    private static async Task LogHolderAsync(
        NpgsqlConnection conn, string lockName, int attempt, ILogger log, CancellationToken ct)
    {
        try
        {
            await using var cmd = NpgsqlCatalog.Command(conn, null, "advisory.named_holders", lockName);
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
