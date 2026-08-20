using global::Npgsql;

namespace Laplace.SubstrateCRUD.Npgsql;

/// <summary>
/// The one open-connection / create-command / set-timeout / bind / read / translate
/// block. It was hand-copied roughly a hundred times across the app — every copy
/// identical but for the SQL, the binds and the row map, and every copy free to drift
/// on the parts that matter (whether the timeout is set, whether the reader is
/// disposed, what a <see cref="PostgresException"/> turns into).
///
/// Translation is a caller-supplied delegate rather than a fixed exception type:
/// Laplace.Substrate is referenced *by* the endpoint assemblies, so the HTTP-facing
/// exceptions (SubstrateQueryException, SubstrateUnavailableException in
/// Laplace.Endpoints.OpenAICompat) cannot be named here without a reference cycle.
/// Pass <paramref name="onError"/> to map failures into whatever the caller's layer
/// raises; omit it and the raw Npgsql exception propagates, which is what call sites
/// that never translated always did.
/// </summary>
public static class NpgsqlRead
{
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
    /// Same as the <see cref="NpgsqlDataSource"/> overload, but on an already-open
    /// connection — for multi-command scopes (TEMP TABLE then SELECT) where opening a
    /// fresh connection would lose session state.
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
            await cmd.PrepareAsync(ct).ConfigureAwait(false);

            var rows = new List<T>(16);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(map(reader));
            return rows;
        }
        catch (Exception ex) when (Translatable(ex, onError))
        {
            throw onError!(ex, label ?? "query");
        }
    }

    /// <summary>
    /// The first row mapped, or <c>null</c> when the command returns none. Rows past the
    /// first are not read — this is the "SELECT ... WHERE id = @id" shape, and the
    /// distinction between no row and a mapped row is the caller's answer.
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
    /// Only database failures are translated, and only when a translator was supplied.
    /// <see cref="PostgresException"/> derives from <see cref="NpgsqlException"/>, so a
    /// translator that wants to tell "the server rejected the query" from "the server is
    /// unreachable" tests for it first — exactly as the hand-rolled copies did.
    /// </summary>
    private static bool Translatable(Exception ex, ErrorTranslator? onError) =>
        onError is not null && ex is NpgsqlException or TimeoutException;
}
