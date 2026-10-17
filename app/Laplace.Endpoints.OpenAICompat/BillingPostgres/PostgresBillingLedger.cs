using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingLedger : IBillingLedger
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBillingLedger(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task RecordAsync(BillingUsageRecord record, CancellationToken ct)
    {
        await using var cmd = NpgsqlCatalog.Command(
            _dataSource,
            "billing.record_usage",
            record.QuoteId,
            record.Tenant,
            record.ServiceId,
            record.Units,
            record.AmountCents,
            record.ExecutedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<BillingUsageRecord>> GetByTenantAsync(string tenant, CancellationToken ct)
    {
        await using var cmd = NpgsqlCatalog.Command(_dataSource, "billing.usage_by_tenant", tenant);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<BillingUsageRecord>();
        while (await reader.ReadAsync(ct))
            rows.Add(Read(reader));
        return rows;
    }

    private static BillingUsageRecord Read(NpgsqlDataReader reader) => new(
        QuoteId: reader.GetString(0),
        Tenant: reader.GetString(1),
        ServiceId: reader.GetString(2),
        Units: reader.GetInt32(3),
        AmountCents: reader.GetInt64(4),
        ExecutedAt: reader.GetFieldValue<DateTimeOffset>(5));

    public async Task RecordRoyaltiesAsync(IReadOnlyList<BillingRoyaltyRecord> records, CancellationToken ct)
    {
        if (records.Count == 0)
            return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        foreach (var r in records)
        {
            await using var cmd = NpgsqlCatalog.Command(
                conn,
                null,
                "billing.record_royalty",
                r.QuoteId,
                r.PayerTenant,
                r.ServiceId,
                r.SourceIdHex,
                (object?)r.SourceOwnerTenant ?? DBNull.Value,
                r.ShareBp,
                r.RoyaltyCents,
                r.HitCount,
                r.TotalWeight,
                r.ExecutedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesByTenantAsync(string tenant, CancellationToken ct)
    {
        await using var cmd = NpgsqlCatalog.Command(_dataSource, "billing.royalties_by_tenant", tenant);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<BillingRoyaltyRecord>();
        while (await reader.ReadAsync(ct))
            rows.Add(ReadRoyalty(reader));
        return rows;
    }

    public async Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesBySourceAsync(string sourceIdHex, CancellationToken ct)
    {
        await using var cmd = NpgsqlCatalog.Command(_dataSource, "billing.royalties_by_source", sourceIdHex.ToLowerInvariant());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<BillingRoyaltyRecord>();
        while (await reader.ReadAsync(ct))
            rows.Add(ReadRoyalty(reader));
        return rows;
    }

    private static BillingRoyaltyRecord ReadRoyalty(NpgsqlDataReader reader) => new(
        QuoteId: reader.GetString(0),
        PayerTenant: reader.GetString(1),
        ServiceId: reader.GetString(2),
        SourceIdHex: reader.GetString(3),
        SourceOwnerTenant: reader.IsDBNull(4) ? null : reader.GetString(4),
        ShareBp: reader.GetInt32(5),
        RoyaltyCents: reader.GetInt64(6),
        HitCount: reader.GetInt64(7),
        TotalWeight: reader.GetDouble(8),
        ExecutedAt: reader.GetFieldValue<DateTimeOffset>(9));
}
