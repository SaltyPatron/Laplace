using global::Npgsql;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// Open-connection / create-command / set-timeout / bind / read / translate for every
/// managed SQL call, so timeout, disposal and error mapping behave the same everywhere.
/// Catalog-query overloads also check each bind against the native catalog's declared
/// parameter types.
///
/// Error translation is a caller-supplied <see cref="ErrorTranslator"/> because the
/// endpoint assemblies that define the surface exceptions reference this one, not the
/// reverse. Without a translator the Npgsql exception propagates unchanged.
/// </summary>
public static class NpgsqlRead
{
    /// <summary>A bound, catalog-validated command for callers that stream rows themselves.
    /// The caller owns execution and disposal.</summary>
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
        NpgsqlCommand command, Func<NpgsqlDataReader,T> map, CancellationToken ct)
    {
        await command.PrepareAsync(ct).ConfigureAwait(false);
        var rows = new List<T>(16);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(map(reader));
        return rows;
    }

    /// <summary>Two independent catalog reads in one batch round trip, each with its own row map.</summary>
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
            var firstCommand = NpgsqlCatalog.Command(first, bindFirst);
            batch.BatchCommands.Add(firstCommand);
            var secondCommand = NpgsqlCatalog.Command(second, bindSecond);
            batch.BatchCommands.Add(secondCommand);
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

    /// <summary>
    /// Maps a failed command into the caller's own exception type. <paramref name="failure"/>
    /// is the original (typically <see cref="PostgresException"/>, <see cref="NpgsqlException"/>
    /// or <see cref="TimeoutException"/>); <paramref name="label"/> is the caller's name for
    /// the query. Returning the exception (rather than throwing it) keeps the throw site here.
    /// </summary>
    public delegate Exception ErrorTranslator(Exception failure, string label);

    /// <summary>Every row the command returns, mapped in order.</summary>
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

    /// <summary>
    /// Same as the <see cref="NpgsqlDataSource"/> overload, on an already-open connection,
    /// so session state (e.g. a TEMP TABLE created earlier) is visible to the read.
    /// </summary>
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

    /// <summary>
    /// The first row mapped, or <c>null</c> when the command returns none. Rows past the
    /// first are not read.
    /// </summary>
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

    /// <inheritdoc cref="ReadFirstOrDefaultAsync{T}(NpgsqlDataSource, string, Func{NpgsqlDataReader, T}, Action{NpgsqlParameterCollection}?, int, CancellationToken, string?, ErrorTranslator?)"/>
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

    /// <summary>
    /// The first column of the first row, or <c>default</c> when the command returns no
    /// row or a SQL NULL. A value type <typeparamref name="T"/> cannot tell those apart
    /// from a genuine zero — ask for <c>ExecuteScalarAsync&lt;long?&gt;</c> when the
    /// difference matters.
    /// </summary>
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

    /// <inheritdoc cref="ExecuteScalarAsync{T}(NpgsqlDataSource, string, Action{NpgsqlParameterCollection}?, int, CancellationToken, string?, ErrorTranslator?)"/>
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

    /// <summary>Rows affected, as reported by the command.</summary>
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

    /// <inheritdoc cref="ExecuteNonQueryAsync(NpgsqlDataSource, string, Action{NpgsqlParameterCollection}?, int, CancellationToken, string?, ErrorTranslator?)"/>
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

            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? "query");
        }
    }

    /// <summary>
    /// Only database failures and timeouts are translated, and only when a translator was
    /// supplied. <see cref="PostgresException"/> derives from <see cref="NpgsqlException"/>,
    /// so a translator that distinguishes a rejected query from an unreachable server
    /// tests for it first.
    /// </summary>
    private static bool Translatable(Exception ex, ErrorTranslator? onError) =>
        onError is not null && ex is NpgsqlException or TimeoutException;
}
