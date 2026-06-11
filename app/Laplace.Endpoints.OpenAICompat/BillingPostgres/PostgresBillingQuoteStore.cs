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
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        Bind(cmd, quote);
        await cmd.ExecuteNonQueryAsync(ct);
        return quote;
    }

    public async Task<BillingQuote?> TryGetAsync(string quoteId, CancellationToken ct)
    {
        const string sql = """
            SELECT quote_id, tenant, service_id, units, amount_cents, currency, status,
                   consumed, stripe_session_id, stripe_checkout_url, created_at, expires_at
            FROM app.billing_quotes
            WHERE quote_id = @quote_id;
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("quote_id", quoteId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new BillingQuote(
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

    public Task<BillingQuote> UpdateAsync(BillingQuote quote, CancellationToken ct) =>
        PutAsync(quote, ct);

    private static void Bind(NpgsqlCommand cmd, BillingQuote quote)
    {
        cmd.Parameters.AddWithValue("quote_id", quote.QuoteId);
        cmd.Parameters.AddWithValue("tenant", quote.Tenant);
        cmd.Parameters.AddWithValue("service_id", quote.ServiceId);
        cmd.Parameters.AddWithValue("units", quote.Units);
        cmd.Parameters.AddWithValue("amount_cents", quote.AmountCents);
        cmd.Parameters.AddWithValue("currency", quote.Currency);
        cmd.Parameters.AddWithValue("status", quote.Status);
        cmd.Parameters.AddWithValue("consumed", quote.Consumed);
        cmd.Parameters.AddWithValue("stripe_session_id", (object?)quote.StripeSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("stripe_checkout_url", (object?)quote.StripeCheckoutUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("created_at", quote.CreatedAt);
        cmd.Parameters.AddWithValue("expires_at", quote.ExpiresAt);
    }
}
