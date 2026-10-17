using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingQuoteStore : IBillingQuoteStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBillingQuoteStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<BillingQuote> PutAsync(BillingQuote quote, CancellationToken ct)
    {
        await using var cmd = NpgsqlCatalog.Command(
            _dataSource,
            "billing.put_quote",
            quote.QuoteId,
            quote.Tenant,
            quote.ServiceId,
            quote.Units,
            quote.AmountCents,
            quote.Currency,
            quote.Status,
            quote.Consumed,
            quote.StripeSessionId,
            quote.StripeCheckoutUrl,
            quote.CreatedAt,
            quote.ExpiresAt);
        await cmd.ExecuteNonQueryAsync(ct);
        return quote;
    }

    public async Task<BillingQuote?> TryGetAsync(string quoteId, CancellationToken ct)
    {
        await using var cmd = NpgsqlCatalog.Command(_dataSource, "billing.get_quote", quoteId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public Task<BillingQuote> UpdateAsync(BillingQuote quote, CancellationToken ct) =>
        PutAsync(quote, ct);

    private static BillingQuote Read(NpgsqlDataReader reader) => new(
        QuoteId: reader.GetString(0),
        Tenant: reader.GetString(1),
        ServiceId: reader.GetString(2),
        Units: reader.GetInt32(3),
        AmountCents: reader.GetInt64(4),
        Currency: reader.GetString(5),
        Status: reader.GetString(6),
        StripeSessionId: reader.IsDBNull(8) ? null : reader.GetString(8),
        StripeCheckoutUrl: reader.IsDBNull(9) ? null : reader.GetString(9),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(10),
        ExpiresAt: reader.GetFieldValue<DateTimeOffset>(11),
        Consumed: reader.GetBoolean(7));
}
