using global::Npgsql;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// One session-level shared holder for the evidence -> fold continuation window.
/// Automatic recovery takes the same named advisory lock exclusively, so a repair
/// can never reconstruct derived state while a live writer still owes part of that
/// state.  The holder is one explicitly budgeted ingest connection per bulk run.
/// </summary>
internal sealed class NpgsqlFoldRecoveryGuard : IAsyncDisposable
{
    internal const string LockName = "laplace_fold_recovery";
    private static readonly TimeSpan WaitWindow = TimeSpan.FromSeconds(30);

    private readonly NpgsqlConnection _connection;
    private readonly ILogger _log;
    private bool _held;

    private NpgsqlFoldRecoveryGuard(NpgsqlConnection connection, ILogger log)
    {
        _connection = connection;
        _log = log;
    }

    internal static async Task<NpgsqlFoldRecoveryGuard> AcquireSharedAsync(
        NpgsqlDataSource dataSource, ILogger log, CancellationToken ct)
    {
        var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var guard = new NpgsqlFoldRecoveryGuard(connection, log);
        try
        {
            await guard.AcquireAsync(ct).ConfigureAwait(false);
            return guard;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AcquireAsync(CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await SetLockTimeoutAsync(WaitWindow, ct).ConfigureAwait(false);
            try
            {
                await using var command = _connection.CreateCommand();
                command.CommandTimeout = 0;
                command.CommandText =
                    "SELECT pg_advisory_lock_shared(hashtextextended($1, 0))";
                command.Parameters.AddWithValue(LockName);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _held = true;
                await SetLockTimeoutAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
                return;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                _log.LogWarning(
                    "fold recovery guard still held exclusively after ~{Seconds}s (attempt {Attempt}); "
                    + "waiting for the active idempotent recovery transaction to finish",
                    (int)WaitWindow.TotalSeconds * attempt, attempt);
            }
        }
    }

    private async Task SetLockTimeoutAsync(TimeSpan timeout, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = timeout == TimeSpan.Zero
            ? "SET lock_timeout = 0"
            : $"SET lock_timeout = '{(int)timeout.TotalSeconds}s'";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_held)
        {
            try
            {
                await using var command = _connection.CreateCommand();
                command.CommandTimeout = 30;
                command.CommandText =
                    "SELECT pg_advisory_unlock_shared(hashtextextended($1, 0))";
                command.Parameters.AddWithValue(LockName);
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _held = false;
            }
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Durable protocol around <c>laplace.ingest_flush_journal</c>.  Evidence commit,
/// relation-scope publication, fold completion, and idempotent repair are separate
/// state transitions; none is inferred from another.
/// </summary>
internal static class NpgsqlFoldJournal
{
    private static readonly TimeSpan RecoveryWaitWindow = TimeSpan.FromSeconds(30);

    internal static Hash128 WorkingSetToken(IReadOnlyList<SubstrateChange> changes)
    {
        var bytes = new byte[checked(changes.Count * 16)];
        for (int i = 0; i < changes.Count; i++)
            changes[i].Metadata.IntentId.WriteBytes(bytes.AsSpan(i * 16, 16));
        return Hash128.Blake3(bytes);
    }

    internal static Hash128[] RelationScope(
        Dictionary<(Hash128 S, Hash128 T, Hash128? O), object>? _)
        => throw new NotSupportedException("Use RelationScope(IEnumerable<Hash128>)");

    internal static Hash128[] RelationScope(IEnumerable<Hash128> types)
    {
        var distinct = types.Distinct().ToArray();
        Array.Sort(distinct, static (a, b) => a.CompareToBytewise(b));
        return distinct;
    }

    internal static async Task RecordScopeAsync(
        NpgsqlDataSource dataSource,
        Hash128 workingSetId,
        Hash128 sourceId,
        IReadOnlyList<Hash128> relationTypes,
        bool recoverable,
        CancellationToken ct)
    {
        var typeBytes = new byte[relationTypes.Count][];
        for (int i = 0; i < relationTypes.Count; i++)
            typeBytes[i] = relationTypes[i].ToBytes();

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE laplace.ingest_flush_journal "
            + "SET fold_type_ids = $2, fold_recoverable = $3 "
            + "WHERE working_set_id = $1 AND source_id = $4 AND NOT folded";
        command.Parameters.Add(new NpgsqlParameter
        { Value = workingSetId.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter
        { Value = typeBytes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter
        { Value = recoverable, NpgsqlDbType = NpgsqlDbType.Boolean });
        command.Parameters.Add(new NpgsqlParameter
        { Value = sourceId.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
        int changed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (changed != 1)
            throw new InvalidOperationException(
                $"fold scope publication expected one unfinished journal token, updated {changed}");
    }

    internal static async Task MarkCompletedAsync(
        NpgsqlDataSource dataSource,
        Hash128 workingSetId,
        Hash128 sourceId,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE laplace.ingest_flush_journal "
            + "SET folded = true, folded_at = clock_timestamp() "
            + "WHERE working_set_id = $1 AND source_id = $2 AND NOT folded";
        command.Parameters.Add(new NpgsqlParameter
        { Value = workingSetId.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter
        { Value = sourceId.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
        int changed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (changed != 1)
            throw new InvalidOperationException(
                $"fold acknowledgement expected one unfinished journal token, updated {changed}");
    }

    /// <summary>
    /// Opportunistic process-crash recovery at bulk-run startup.  SQL returns -1
    /// when another current writer owns the shared recovery guard; that is not a
    /// failure and does not serialize independent live ingests.
    /// </summary>
    internal static async Task<long> RecoverAllQuiescentAsync(
        NpgsqlDataSource dataSource, ILogger log, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = "SELECT ops.recover_unfolded_all_quiescent()";
        long recovered = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        if (recovered < 0)
            log.LogDebug("unfolded-fold startup recovery deferred: another live writer holds the shared guard");
        else if (recovered > 0)
            log.LogWarning("recovered {Tokens} unfinished fold journal token(s) before bulk ingest", recovered);
        return recovered;
    }

    internal static async Task<long> RecoverSourceAsync(
        NpgsqlDataSource dataSource, Hash128 sourceId, ILogger log, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await using (var timeout = connection.CreateCommand())
            {
                timeout.CommandText =
                    $"SET lock_timeout = '{(int)RecoveryWaitWindow.TotalSeconds}s'";
                await timeout.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 0;
                command.CommandText = "SELECT ops.recover_unfolded_source($1)";
                command.Parameters.Add(new NpgsqlParameter
                { Value = sourceId.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
                long recovered = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
                await using var clear = connection.CreateCommand();
                clear.CommandText = "SET lock_timeout = 0";
                await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (recovered > 0)
                    log.LogWarning(
                        "idempotently reconstructed {Tokens} unfinished fold token(s) for source {Source}",
                        recovered, sourceId);
                return recovered;
            }
            catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                log.LogWarning(
                    "unfinished fold recovery for source {Source} is waiting for another live writer "
                    + "after ~{Seconds}s (attempt {Attempt})",
                    sourceId, (int)RecoveryWaitWindow.TotalSeconds * attempt, attempt);
            }
        }
    }

    internal static async Task<long> CountUnfoldedSourceAsync(
        NpgsqlDataSource dataSource, Hash128 sourceId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM laplace.ingest_flush_journal "
            + "WHERE source_id = $1 AND NOT folded";
        command.Parameters.Add(new NpgsqlParameter
        { Value = sourceId.ToBytes(), NpgsqlDbType = NpgsqlDbType.Bytea });
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
    }
}
