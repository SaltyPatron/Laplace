using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingLedger : IBillingLedger
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBillingLedger(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task RecordAsync(BillingUsageRecord record, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.billing_usage (quote_id, tenant, service_id, units, amount_cents, executed_at)
            VALUES (@quote_id, @tenant, @service_id, @units, @amount_cents, @executed_at);
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("quote_id", record.QuoteId);
        cmd.Parameters.AddWithValue("tenant", record.Tenant);
        cmd.Parameters.AddWithValue("service_id", record.ServiceId);
        cmd.Parameters.AddWithValue("units", record.Units);
        cmd.Parameters.AddWithValue("amount_cents", record.AmountCents);
        cmd.Parameters.AddWithValue("executed_at", record.ExecutedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<BillingUsageRecord>> GetByTenantAsync(string tenant, CancellationToken ct)
    {
        const string sql = """
            SELECT quote_id, tenant, service_id, units, amount_cents, executed_at
            FROM app.billing_usage
            WHERE tenant = @tenant
            ORDER BY executed_at DESC;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant", tenant);
        var records = new List<BillingUsageRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new BillingUsageRecord(
                QuoteId: reader.GetString(0),
                Tenant: reader.GetString(1),
                ServiceId: reader.GetString(2),
                Units: reader.GetInt32(3),
                AmountCents: reader.GetInt64(4),
                ExecutedAt: reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return records;
    }
}
