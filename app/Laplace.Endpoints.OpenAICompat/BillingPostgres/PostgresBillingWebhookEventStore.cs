using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat.BillingPostgres;

internal sealed class PostgresBillingWebhookEventStore : IBillingWebhookEventStore
{
    private readonly NpgsqlDataSource _dataSource;
    public PostgresBillingWebhookEventStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> TryBeginAsync(string eventId, string eventType, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.billing_webhook_events (event_id, status, updated_at)
            VALUES (@event_id, @status, now())
            ON CONFLICT (event_id) DO UPDATE
                SET status = EXCLUDED.status, updated_at = now()
            WHERE app.billing_webhook_events.status = 'retryable'
               OR (app.billing_webhook_events.status LIKE 'processing:%'
                   AND app.billing_webhook_events.updated_at < now() - interval '5 minutes');
            """;
        return await NpgsqlRead.ExecuteNonQueryAsync(_dataSource, sql, p =>
        {
            p.AddWithValue("event_id", eventId);
            p.AddWithValue("status", $"processing:{eventType}");
        }, ct: ct) == 1;
    }

    public Task CompleteAsync(string eventId, string status, CancellationToken ct) =>
        NpgsqlRead.ExecuteNonQueryAsync(_dataSource,
            "UPDATE app.billing_webhook_events SET status = @status, updated_at = now() WHERE event_id = @event_id;",
            p =>
            {
                p.AddWithValue("event_id", eventId);
                p.AddWithValue("status", status);
            }, ct: ct);

    public Task<string?> GetStatusAsync(string eventId, CancellationToken ct) =>
        NpgsqlRead.ReadFirstOrDefaultAsync(_dataSource,
            "SELECT status FROM app.billing_webhook_events WHERE event_id = @event_id;",
            r => r.GetString(0), p => p.AddWithValue("event_id", eventId), ct: ct);
}
