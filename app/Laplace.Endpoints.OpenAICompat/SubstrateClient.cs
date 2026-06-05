using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal sealed class SubstrateClient : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public SubstrateClient()
    {
        var connString = BuildConnectionString();
        _dataSource = new NpgsqlDataSourceBuilder(connString).Build();
    }

    public async Task<ConverseRow?> ConverseAsync(string prompt, CancellationToken ct)
    {
        const string sql = "SELECT reply, eff_mu, witnesses FROM laplace.converse(@p) LIMIT 1;";
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("p", prompt);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            var reply = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var mu = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            var witnesses = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            return new ConverseRow(reply, mu, witnesses);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate conversation query failed.", ex);
        }
    }

    public async Task<IReadOnlyList<CompletionRow>> CompletionsAsync(string prompt, int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT
                encode(c.object_id, 'hex') AS object_id_hex,
                encode(c.kind_id, 'hex') AS kind_id_hex,
                c.eff_mu,
                c.witnesses,
                COALESCE(laplace.label(c.object_id), encode(c.object_id, 'hex')) AS object_label
            FROM laplace.completions(laplace.word_id(@prompt), @limit) c
            ORDER BY c.eff_mu DESC;
            """;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("prompt", prompt);
            cmd.Parameters.AddWithValue("limit", Math.Max(1, limit));

            var rows = new List<CompletionRow>(Math.Max(1, limit));
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new CompletionRow(
                    ObjectIdHex: reader.GetString(0),
                    KindIdHex: reader.GetString(1),
                    EffectiveMu: reader.GetDecimal(2),
                    Witnesses: reader.GetInt64(3),
                    ObjectLabel: reader.GetString(4)));
            }

            return rows;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new SubstrateUnavailableException("Substrate completions query failed.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    private static string BuildConnectionString()
    {
        // Keep parity with CLI safety defaults: unset LAPLACE_DB targets laplace-dev.
        var s = Environment.GetEnvironmentVariable("LAPLACE_DB")
            ?? "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace-dev";
        if (!s.Contains("Include Error Detail", StringComparison.OrdinalIgnoreCase))
            s += ";Include Error Detail=true";
        if (!s.Contains("Search Path", StringComparison.OrdinalIgnoreCase))
            s += ";Search Path=laplace,public";
        return s;
    }
}

internal sealed class SubstrateUnavailableException : Exception
{
    public SubstrateUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
