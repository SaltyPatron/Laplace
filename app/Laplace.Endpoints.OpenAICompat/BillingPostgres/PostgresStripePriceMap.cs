using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresStripePriceMap : IStripePriceMap
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresStripePriceMap(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public Task<string?> TryGetAsync(string lookupKey, CancellationToken ct)
    {
        const string sql = "SELECT stripe_price_id FROM app.stripe_price_map WHERE lookup_key = @lookup_key;";
        return NpgsqlRead.ExecuteScalarAsync<string>(_dataSource, sql,
            p => p.AddWithValue("lookup_key", lookupKey), ct: ct);
    }

    public Task SetAsync(string lookupKey, string stripePriceId, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.stripe_price_map (lookup_key, stripe_price_id, updated_at)
            VALUES (@lookup_key, @stripe_price_id, now())
            ON CONFLICT (lookup_key) DO UPDATE SET
                stripe_price_id = EXCLUDED.stripe_price_id,
                updated_at = now();
            """;
        return NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql, p =>
        {
            p.AddWithValue("lookup_key", lookupKey);
            p.AddWithValue("stripe_price_id", stripePriceId);
        }, ct: ct);
    }
}
