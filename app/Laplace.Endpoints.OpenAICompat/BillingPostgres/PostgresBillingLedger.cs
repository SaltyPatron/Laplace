using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingLedger : IBillingLedger
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBillingLedger(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public Task RecordAsync(BillingUsageRecord record, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.billing_usage (quote_id, tenant, service_id, units, amount_cents, executed_at)
            VALUES (@quote_id, @tenant, @service_id, @units, @amount_cents, @executed_at);
            """;
        return NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql, p =>
        {
            p.AddWithValue("quote_id", record.QuoteId);
            p.AddWithValue("tenant", record.Tenant);
            p.AddWithValue("service_id", record.ServiceId);
            p.AddWithValue("units", record.Units);
            p.AddWithValue("amount_cents", record.AmountCents);
            p.AddWithValue("executed_at", record.ExecutedAt);
        }, ct: ct);
    }

    public Task<IReadOnlyList<BillingUsageRecord>> GetByTenantAsync(string tenant, CancellationToken ct)
    {
        const string sql = """
            SELECT quote_id, tenant, service_id, units, amount_cents, executed_at
            FROM app.billing_usage
            WHERE tenant = @tenant
            ORDER BY executed_at DESC;
            """;
        return NpgsqlRead.ReadRowsAsync(_dataSource, sql, Read,
            p => p.AddWithValue("tenant", tenant), ct: ct);
    }

    private static BillingUsageRecord Read(NpgsqlDataReader reader) => new(
        QuoteId: reader.GetString(0),
        Tenant: reader.GetString(1),
        ServiceId: reader.GetString(2),
        Units: reader.GetInt32(3),
        AmountCents: reader.GetInt64(4),
        ExecutedAt: reader.GetFieldValue<DateTimeOffset>(5));
}
