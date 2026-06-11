using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresStripePriceMap : IStripePriceMap
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresStripePriceMap(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<string?> TryGetAsync(string lookupKey, CancellationToken ct)
    {
        const string sql = "SELECT stripe_price_id FROM app.stripe_price_map WHERE lookup_key = @lookup_key;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("lookup_key", lookupKey);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value as string;
    }

    public async Task SetAsync(string lookupKey, string stripePriceId, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.stripe_price_map (lookup_key, stripe_price_id, updated_at)
            VALUES (@lookup_key, @stripe_price_id, now())
            ON CONFLICT (lookup_key) DO UPDATE SET
                stripe_price_id = EXCLUDED.stripe_price_id,
                updated_at = now();
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("lookup_key", lookupKey);
        cmd.Parameters.AddWithValue("stripe_price_id", stripePriceId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
