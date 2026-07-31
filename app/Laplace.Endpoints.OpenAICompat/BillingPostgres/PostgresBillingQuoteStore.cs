using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingQuoteStore : IBillingQuoteStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBillingQuoteStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<BillingQuote> PutAsync(BillingQuote quote, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.billing_quotes
                (quote_id, tenant, service_id, units, amount_cents, currency, status,
                 consumed, stripe_session_id, stripe_checkout_url, created_at, expires_at)
            VALUES (@quote_id, @tenant, @service_id, @units, @amount_cents, @currency, @status,
                    @consumed, @stripe_session_id, @stripe_checkout_url, @created_at, @expires_at)
            ON CONFLICT (quote_id) DO UPDATE SET
                tenant = EXCLUDED.tenant,
                service_id = EXCLUDED.service_id,
                units = EXCLUDED.units,
                amount_cents = EXCLUDED.amount_cents,
                currency = EXCLUDED.currency,
                status = EXCLUDED.status,
                consumed = EXCLUDED.consumed,
                stripe_session_id = EXCLUDED.stripe_session_id,
                stripe_checkout_url = EXCLUDED.stripe_checkout_url,
                created_at = EXCLUDED.created_at,
                expires_at = EXCLUDED.expires_at;
            """;
        await NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql, p => Bind(p, quote), ct: ct);
        return quote;
    }

    public Task<BillingQuote?> TryGetAsync(string quoteId, CancellationToken ct)
    {
        const string sql = """
            SELECT quote_id, tenant, service_id, units, amount_cents, currency, status,
                   consumed, stripe_session_id, stripe_checkout_url, created_at, expires_at
            FROM app.billing_quotes
            WHERE quote_id = @quote_id;
            """;
        return NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource, sql, Read,
            p => p.AddWithValue("quote_id", quoteId), ct: ct);
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

    private static void Bind(NpgsqlParameterCollection p, BillingQuote quote)
    {
        p.AddWithValue("quote_id", quote.QuoteId);
        p.AddWithValue("tenant", quote.Tenant);
        p.AddWithValue("service_id", quote.ServiceId);
        p.AddWithValue("units", quote.Units);
        p.AddWithValue("amount_cents", quote.AmountCents);
        p.AddWithValue("currency", quote.Currency);
        p.AddWithValue("status", quote.Status);
        p.AddWithValue("consumed", quote.Consumed);
        p.AddWithValue("stripe_session_id", (object?)quote.StripeSessionId ?? DBNull.Value);
        p.AddWithValue("stripe_checkout_url", (object?)quote.StripeCheckoutUrl ?? DBNull.Value);
        p.AddWithValue("created_at", quote.CreatedAt);
        p.AddWithValue("expires_at", quote.ExpiresAt);
    }
}
