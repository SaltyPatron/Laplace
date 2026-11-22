using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.Data;

/// <summary>
/// Shared connection, command preparation, timeout, and error-translation wrappers for all
/// read-only PostgreSQL substrate operations.
/// </summary>
public static class NpgsqlRead
{
    public delegate Exception ErrorTranslator(Exception failure, string label);

    public static NpgsqlCommand CreateCommand(
        NpgsqlDataSource source, NativeSqlQuery query,
        Action<NpgsqlParameterCollection>? bind = null, int timeoutSeconds = 0)
        => NpgsqlCatalog.Command(source, query, bind, timeoutSeconds);

    public static async Task<T?> ExecuteScalarAsync<T>(
        NpgsqlDataSource source, NativeSqlQuery query,
        Action<NpgsqlParameterCollection>? bind = null, int timeoutSeconds = 0,
        CancellationToken ct = default, string? label = null, ErrorTranslator? onError = null)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
            return await ExecuteScalarAsync<T>(connection, query, bind, timeoutSeconds, ct,
                label ?? query.Name, null).ConfigureAwait(false);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? query.Name);
        }
    }

    public static async Task<T?> ExecuteScalarAsync<T>(
        NpgsqlConnection connection, NativeSqlQuery query,
        Action<NpgsqlParameterCollection>? bind = null, int timeoutSeconds = 0,
        CancellationToken ct = default, string? label = null, ErrorTranslator? onError = null)
    {
        try
        {
            await using var command = NpgsqlCatalog.Command(connection, null, query, bind, timeoutSeconds);
            await command.PrepareAsync(ct).ConfigureAwait(false);
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is T typed ? typed : default;
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? query.Name);
        }
    }

    public static async Task<IReadOnlyList<T>> ReadRowsAsync<T>(
        NpgsqlConnection conn, NativeSqlQuery query, Func<NpgsqlDataReader, T> map,
        Action<NpgsqlParameterCollection>? bind = null, int timeoutSeconds = 0,
        CancellationToken ct = default, string? label = null, ErrorTranslator? onError = null)
    {
        try
        {
            await using var command = NpgsqlCatalog.Command(conn, null, query, bind, timeoutSeconds);
            return await ReadCommandRowsAsync(command, map, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? query.Name);
        }
    }

    public static async Task<IReadOnlyList<T>> ReadRowsAsync<T>(
        NpgsqlDataSource source, NativeSqlQuery query, Func<NpgsqlDataReader, T> map,
        Action<NpgsqlParameterCollection>? bind = null, int timeoutSeconds = 0,
        CancellationToken ct = default, string? label = null, ErrorTranslator? onError = null)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
            return await ReadRowsAsync(connection, query, map, bind, timeoutSeconds, ct,
                label ?? query.Name, null).ConfigureAwait(false);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? query.Name);
        }
    }

    private static async Task<IReadOnlyList<T>> ReadCommandRowsAsync<T>(
        NpgsqlCommand command, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        await command.PrepareAsync(ct).ConfigureAwait(false);
        var rows = new List<T>(16);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(map(reader));
        return rows;
    }

    public static async Task<(IReadOnlyList<TFirst> First, IReadOnlyList<TSecond> Second)>
        ReadBatchRowsAsync<TFirst, TSecond>(
        NpgsqlConnection conn,
        NativeSqlQuery first, Action<NpgsqlParameterCollection> bindFirst, Func<NpgsqlDataReader, TFirst> mapFirst,
        NativeSqlQuery second, Action<NpgsqlParameterCollection> bindSecond, Func<NpgsqlDataReader, TSecond> mapSecond,
        CancellationToken ct = default, int timeoutSeconds = 30, ErrorTranslator? onError = null)
    {
        try
        {
            await using var batch = new NpgsqlBatch(conn) { Timeout = timeoutSeconds };
            batch.BatchCommands.Add(NpgsqlCatalog.Command(first, bindFirst));
            batch.BatchCommands.Add(NpgsqlCatalog.Command(second, bindSecond));
            await batch.PrepareAsync(ct).ConfigureAwait(false);
            await using var reader = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var firstRows = new List<TFirst>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) firstRows.Add(mapFirst(reader));
            if (!await reader.NextResultAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException($"{second.Name}: missing batch result.");
            var secondRows = new List<TSecond>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) secondRows.Add(mapSecond(reader));
            return (firstRows, secondRows);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, first.Name + "+" + second.Name);
        }
    }

    public static async Task<IReadOnlyList<T>> ReadRowsAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        Func<NpgsqlDataReader, T> map,
        Action<NpgsqlParameterCollection>? bind = null,
        int timeoutSeconds = 0,
        CancellationToken ct = default,
        string? label = null,
        ErrorTranslator? onError = null)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            return await ReadRowsAsync(conn, sql, map, bind, timeoutSeconds, ct, label, onError: null)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? "query");
        }
    }

    public static async Task<IReadOnlyList<T>> ReadRowsAsync<T>(
        NpgsqlConnection conn,
        string sql,
        Func<NpgsqlDataReader, T> map,
        Action<NpgsqlParameterCollection>? bind = null,
        int timeoutSeconds = 0,
        CancellationToken ct = default,
        string? label = null,
        ErrorTranslator? onError = null)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            if (timeoutSeconds > 0) cmd.CommandTimeout = timeoutSeconds;
            bind?.Invoke(cmd.Parameters);
            return await ReadCommandRowsAsync(cmd, map, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? "query");
        }
    }

    public static async Task<T?> ReadFirstOrDefaultAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        Func<NpgsqlDataReader, T> map,
        Action<NpgsqlParameterCollection>? bind = null,
        int timeoutSeconds = 0,
        CancellationToken ct = default,
        string? label = null,
        ErrorTranslator? onError = null)
        where T : class
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            return await ReadFirstOrDefaultAsync(conn, sql, map, bind, timeoutSeconds, ct, label, onError: null)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? "query");
        }
    }

    public static async Task<T?> ReadFirstOrDefaultAsync<T>(
        NpgsqlConnection conn,
        string sql,
        Func<NpgsqlDataReader, T> map,
        Action<NpgsqlParameterCollection>? bind = null,
        int timeoutSeconds = 0,
        CancellationToken ct = default,
        string? label = null,
        ErrorTranslator? onError = null)
        where T : class
    {
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            if (timeoutSeconds > 0) cmd.CommandTimeout = timeoutSeconds;
            bind?.Invoke(cmd.Parameters);
            await cmd.PrepareAsync(ct).ConfigureAwait(false);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? map(reader) : null;
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? "query");
        }
    }

    public static async Task<T?> ExecuteScalarAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        Action<NpgsqlParameterCollection>? bind = null,
        int timeoutSeconds = 0,
        CancellationToken ct = default,
        string? label = null,
        ErrorTranslator? onError = null)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            return await ExecuteScalarAsync<T>(conn, sql, bind, timeoutSeconds, ct, label, onError: null)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? "query");
        }
    }

    public static async Task<T?> ExecuteScalarAsync<T>(
        NpgsqlConnection conn,
        string sql,
        Action<NpgsqlParameterCollection>? bind = null,
        int timeoutSeconds = 0,
        CancellationToken ct = default,
        string? label = null,
        ErrorTranslator? onError = null)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            if (timeoutSeconds > 0) cmd.CommandTimeout = timeoutSeconds;
            bind?.Invoke(cmd.Parameters);
            await cmd.PrepareAsync(ct).ConfigureAwait(false);
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is T typed ? typed : default;
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? "query");
        }
    }

    public static async Task<int> ExecuteNonQueryAsync(
        NpgsqlDataSource dataSource,
        string sql,
        Action<NpgsqlParameterCollection>? bind = null,
        int timeoutSeconds = 0,
        CancellationToken ct = default,
        string? label = null,
        ErrorTranslator? onError = null)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            return await ExecuteNonQueryAsync(conn, sql, bind, timeoutSeconds, ct, label, onError: null)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? "query");
        }
    }

    public static async Task<int> ExecuteNonQueryAsync(
        NpgsqlConnection conn,
        string sql,
        Action<NpgsqlParameterCollection>? bind = null,
        int timeoutSeconds = 0,
        CancellationToken ct = default,
        string? label = null,
        ErrorTranslator? onError = null)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            if (timeoutSeconds > 0) cmd.CommandTimeout = timeoutSeconds;
            bind?.Invoke(cmd.Parameters);
            if (cmd.Parameters.Count > 0 && !sql.Contains(';', StringComparison.Ordinal))
                await cmd.PrepareAsync(ct).ConfigureAwait(false);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? "query");
        }
    }

    private static bool Translatable(Exception ex, ErrorTranslator? onError) =>
        onError is not null && ex is NpgsqlException or TimeoutException;
}
