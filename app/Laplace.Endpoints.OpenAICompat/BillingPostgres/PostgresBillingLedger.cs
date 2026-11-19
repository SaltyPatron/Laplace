using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingLedger : IBillingLedger
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBillingLedger(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task RecordAsync(BillingUsageRecord record, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = await NpgsqlCatalog.PreparedCommandAsync(
            conn,
            null,
            "billing.record_usage",
            ct,
            record.QuoteId,
            record.Tenant,
            record.ServiceId,
            record.Units,
            record.AmountCents,
            record.ExecutedAt).ConfigureAwait(false);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BillingUsageRecord>> GetByTenantAsync(string tenant, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = await NpgsqlCatalog.PreparedCommandAsync(
            conn, null, "billing.usage_by_tenant", ct, tenant).ConfigureAwait(false);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<BillingUsageRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
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

        int n = records.Count;
        var quoteIds = new string[n];
        var payerTenants = new string[n];
        var serviceIds = new string[n];
        var sourceIds = new string[n];
        var sourceOwnerTenants = new string?[n];
        var shareBps = new int[n];
        var royaltyCents = new long[n];
        var hitCounts = new long[n];
        var totalWeights = new double[n];
        var executedAts = new DateTimeOffset[n];
        for (int i = 0; i < n; i++)
        {
            var r = records[i];
            quoteIds[i] = r.QuoteId;
            payerTenants[i] = r.PayerTenant;
            serviceIds[i] = r.ServiceId;
            sourceIds[i] = r.SourceIdHex;
            sourceOwnerTenants[i] = r.SourceOwnerTenant;
            shareBps[i] = r.ShareBp;
            royaltyCents[i] = r.RoyaltyCents;
            hitCounts[i] = r.HitCount;
            totalWeights[i] = r.TotalWeight;
            executedAts[i] = r.ExecutedAt;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = await NpgsqlCatalog.PreparedCommandAsync(
            conn,
            null,
            "billing.record_royalties_batch",
            ct,
            quoteIds,
            payerTenants,
            serviceIds,
            sourceIds,
            sourceOwnerTenants,
            shareBps,
            royaltyCents,
            hitCounts,
            totalWeights,
            executedAts).ConfigureAwait(false);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesByTenantAsync(string tenant, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = await NpgsqlCatalog.PreparedCommandAsync(
            conn, null, "billing.royalties_by_tenant", ct, tenant).ConfigureAwait(false);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<BillingRoyaltyRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(ReadRoyalty(reader));
        return rows;
    }

    public async Task<IReadOnlyList<BillingRoyaltyRecord>> GetRoyaltiesBySourceAsync(string sourceIdHex, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = await NpgsqlCatalog.PreparedCommandAsync(
            conn, null, "billing.royalties_by_source", ct, sourceIdHex.ToLowerInvariant()).ConfigureAwait(false);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<BillingRoyaltyRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
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
