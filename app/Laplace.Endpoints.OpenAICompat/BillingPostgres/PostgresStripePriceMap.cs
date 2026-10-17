using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresStripePriceMap : IStripePriceMap
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresStripePriceMap(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<string?> TryGetAsync(string lookupKey, CancellationToken ct)
    {
        await using var cmd = NpgsqlCatalog.Command(_dataSource, "billing.get_stripe_price", lookupKey);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    public async Task SetAsync(string lookupKey, string stripePriceId, CancellationToken ct)
    {
        await using var cmd = NpgsqlCatalog.Command(_dataSource, "billing.set_stripe_price", lookupKey, stripePriceId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
