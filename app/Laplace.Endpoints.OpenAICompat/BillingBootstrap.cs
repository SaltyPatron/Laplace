using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Npgsql;
using Stripe;

namespace Laplace.Endpoints.OpenAICompat;

internal interface IBillingConfigStore
{
    Task<string?> TryGetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
}

internal sealed class InMemoryBillingConfigStore : IBillingConfigStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> TryGetAsync(string key, CancellationToken ct) =>
        Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string value, CancellationToken ct)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class PostgresBillingConfigStore : IBillingConfigStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresBillingConfigStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<string?> TryGetAsync(string key, CancellationToken ct)
    {
        const string sql = "SELECT value FROM app.billing_config WHERE key = @key;";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO app.billing_config (key, value, updated_at)
            VALUES (@key, @value, now())
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now();
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Webhook signing secret resolution: an explicitly configured STRIPE_WEBHOOK_SECRET
/// (dev `stripe listen`) always wins; otherwise the secret captured when this app
/// provisioned its own webhook endpoint at bootstrap is used.
/// </summary>
internal interface IWebhookSecretProvider
{
    ValueTask<string?> GetAsync(CancellationToken ct);
}

internal sealed class WebhookSecretProvider : IWebhookSecretProvider
{
    public const string ConfigKey = "stripe_webhook_secret";

    private readonly StripeBillingOptions _options;
    private readonly IBillingConfigStore _config;

    public WebhookSecretProvider(IOptions<StripeBillingOptions> options, IBillingConfigStore config)
    {
        _options = options.Value;
        _config = config;
    }

    public async ValueTask<string?> GetAsync(CancellationToken ct) =>
        string.IsNullOrWhiteSpace(_options.WebhookSecret)
            ? await _config.TryGetAsync(ConfigKey, ct)
            : _options.WebhookSecret;
}

internal sealed record WebhookProvisionResult(string Status, string? EndpointId, string? Url);

internal interface IStripeWebhookProvisioner
{
    Task<WebhookProvisionResult> EnsureAsync(CancellationToken ct);
}

internal sealed class StripeWebhookProvisioner : IStripeWebhookProvisioner
{
    private const string EndpointIdConfigKey = "stripe_webhook_endpoint_id";

    private static readonly List<string> RequiredEvents = new()
    {
        "checkout.session.completed",
        "invoice.paid",
        "customer.subscription.deleted",
        "customer.subscription.updated"
    };

    private readonly StripeBillingOptions _options;
    private readonly IBillingConfigStore _config;

    public StripeWebhookProvisioner(IOptions<StripeBillingOptions> options, IBillingConfigStore config)
    {
        _options = options.Value;
        _config = config;
    }

    public async Task<WebhookProvisionResult> EnsureAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return new WebhookProvisionResult("stripe_not_configured", null, null);
        if (string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
            return new WebhookProvisionResult("no_public_base_url", null, null);

        var url = $"{_options.PublicBaseUrl.TrimEnd('/')}/v1/billing/webhooks/stripe";
        StripeConfiguration.ApiKey = _options.ApiKey;
        var service = new WebhookEndpointService();

        try
        {
            var existing = await service.ListAsync(
                new WebhookEndpointListOptions { Limit = 100 }, cancellationToken: ct);
            var match = existing.Data.FirstOrDefault(e =>
                string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.Status, "disabled", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                // The signing secret is only returned at creation. If we created this
                // endpoint (its id matches the one we stored alongside the secret) the
                // stored secret is still valid; a hand-created endpoint we cannot verify.
                await _config.SetAsync(EndpointIdConfigKey, match.Id, ct);
                var haveSecret = await _config.TryGetAsync(WebhookSecretProvider.ConfigKey, ct) is not null
                                 || !string.IsNullOrWhiteSpace(_options.WebhookSecret);
                return new WebhookProvisionResult(haveSecret ? "exists" : "exists_secret_unknown", match.Id, url);
            }

            var created = await service.CreateAsync(new WebhookEndpointCreateOptions
            {
                Url = url,
                EnabledEvents = RequiredEvents,
                Description = "Laplace billing (auto-provisioned)",
                Metadata = new Dictionary<string, string> { ["laplace_managed"] = "true" }
            }, cancellationToken: ct);

            await _config.SetAsync(EndpointIdConfigKey, created.Id, ct);
            await _config.SetAsync(WebhookSecretProvider.ConfigKey, created.Secret, ct);
            return new WebhookProvisionResult("created", created.Id, url);
        }
        catch (StripeException ex)
        {
            return new WebhookProvisionResult($"error:{ex.StripeError?.Code ?? "stripe_error"}", null, url);
        }
    }
}

internal sealed record BillingBootstrapResult(
    string StoreMode,
    bool StripeConfigured,
    bool BillingEnforced,
    StripeCatalogSyncResult Catalog,
    WebhookProvisionResult Webhook);

internal interface IBillingBootstrap
{
    Task<BillingBootstrapResult> RunAsync(CancellationToken ct);
}

/// <summary>
/// The rebuild-itself entry point: idempotently ensures every Stripe object the
/// system sells through (products, prices, the webhook endpoint) exists, keyed by
/// lookup keys and metadata — never by hand-created dashboard ids. Runs at startup
/// and on demand via POST /v1/billing/operator/bootstrap.
/// </summary>
internal sealed class BillingBootstrap : IBillingBootstrap
{
    private readonly IStripeCatalogSync _catalogSync;
    private readonly IStripeWebhookProvisioner _webhooks;
    private readonly StripeBillingOptions _options;
    private readonly BillingStoreMode _storeMode;

    public BillingBootstrap(
        IStripeCatalogSync catalogSync,
        IStripeWebhookProvisioner webhooks,
        IOptions<StripeBillingOptions> options,
        BillingStoreMode storeMode)
    {
        _catalogSync = catalogSync;
        _webhooks = webhooks;
        _options = options.Value;
        _storeMode = storeMode;
    }

    public async Task<BillingBootstrapResult> RunAsync(CancellationToken ct)
    {
        var catalog = await _catalogSync.EnsureAllAsync(ct);
        var webhook = await _webhooks.EnsureAsync(ct);
        return new BillingBootstrapResult(
            StoreMode: _storeMode.Mode,
            StripeConfigured: !string.IsNullOrWhiteSpace(_options.ApiKey),
            BillingEnforced: !_options.Bypass,
            Catalog: catalog,
            Webhook: webhook);
    }
}

/// <summary>Which persistence backend billing resolved to at composition ("postgres"/"memory").</summary>
internal sealed record BillingStoreMode(string Mode, string? Detail);

internal sealed class BillingBootstrapService : BackgroundService
{
    private readonly IBillingBootstrap _bootstrap;
    private readonly ILogger<BillingBootstrapService> _logger;

    public BillingBootstrapService(IBillingBootstrap bootstrap, ILogger<BillingBootstrapService> logger)
    {
        _bootstrap = bootstrap;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await _bootstrap.RunAsync(stoppingToken);
            var synced = result.Catalog.Entries.Count(e =>
                e.Status is "exists" or "created");
            _logger.LogInformation(
                "billing bootstrap: store={Store} stripe={Stripe} enforced={Enforced} catalog={Synced}/{Total} webhook={Webhook}",
                result.StoreMode,
                result.StripeConfigured,
                result.BillingEnforced,
                synced,
                result.Catalog.Entries.Count,
                result.Webhook.Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "billing bootstrap failed; serving continues, re-run via POST /v1/billing/operator/bootstrap");
        }
    }
}
