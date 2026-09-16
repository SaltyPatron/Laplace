using System.Collections.Concurrent;
using Laplace.SubstrateCRUD.Npgsql;
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
    public Task<string?> TryGetAsync(string key, CancellationToken ct) =>
        NpgsqlRead.ExecuteScalarAsync<string>(_dataSource,
            "SELECT value FROM app.billing_config WHERE key = @key;", p => p.AddWithValue("key", key), ct: ct);
    public Task SetAsync(string key, string value, CancellationToken ct) =>
        NpgsqlRead.ExecuteNonQueryAsync(_dataSource, """
            INSERT INTO app.billing_config (key, value, updated_at) VALUES (@key, @value, now())
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now();
            """, p => { p.AddWithValue("key", key); p.AddWithValue("value", value); }, ct: ct);
}

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
        _options = options.Value; _config = config;
    }
    public async ValueTask<string?> GetAsync(CancellationToken ct) =>
        string.IsNullOrWhiteSpace(_options.WebhookSecret) ? await _config.TryGetAsync(ConfigKey, ct) : _options.WebhookSecret;
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
        "checkout.session.completed", "checkout.session.async_payment_succeeded",
        "checkout.session.async_payment_failed", "invoice.paid", "invoice.payment_failed",
        "customer.subscription.created", "customer.subscription.deleted", "customer.subscription.updated",
        "customer.subscription.paused", "customer.subscription.resumed"
    };
    private readonly StripeBillingOptions _options;
    private readonly IBillingConfigStore _config;
    public StripeWebhookProvisioner(IOptions<StripeBillingOptions> options, IBillingConfigStore config)
    {
        _options = options.Value; _config = config;
    }

    public async Task<WebhookProvisionResult> EnsureAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return new("stripe_not_configured", null, null);
        if (!Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            return new("no_public_https_base_url", null, null);
        var url = new Uri(origin, "/v1/billing/webhooks/stripe").AbsoluteUri;
        var service = new WebhookEndpointService(new StripeClient(_options.ApiKey));
        try
        {
            WebhookEndpoint? match = null;
            await foreach (var endpoint in service.ListAutoPagingAsync(new WebhookEndpointListOptions { Limit = 100 }, cancellationToken: ct))
            {
                if (!string.Equals(endpoint.Url, url, StringComparison.OrdinalIgnoreCase)) continue;
                match = endpoint; break;
            }
            if (match is not null)
            {
                if (string.Equals(match.Status, "disabled", StringComparison.OrdinalIgnoreCase))
                    return new("disabled", match.Id, url);
                var storedId = await _config.TryGetAsync(EndpointIdConfigKey, ct);
                var haveSecret = !string.IsNullOrWhiteSpace(_options.WebhookSecret)
                    || (storedId == match.Id && !string.IsNullOrWhiteSpace(await _config.TryGetAsync(WebhookSecretProvider.ConfigKey, ct)));
                var enabled = match.EnabledEvents ?? new List<string>();
                if (!enabled.Contains("*") && RequiredEvents.Except(enabled, StringComparer.Ordinal).Any())
                    await service.UpdateAsync(match.Id, new WebhookEndpointUpdateOptions
                    {
                        EnabledEvents = enabled.Union(RequiredEvents, StringComparer.Ordinal).ToList()
                    }, cancellationToken: ct);
                // Do not relabel a secret from some other endpoint as this one's.
                if (haveSecret) await _config.SetAsync(EndpointIdConfigKey, match.Id, ct);
                return new(haveSecret ? "exists" : "exists_secret_unknown", match.Id, url);
            }
            var created = await service.CreateAsync(new WebhookEndpointCreateOptions
            {
                Url = url, EnabledEvents = RequiredEvents,
                Description = "Laplace billing (auto-provisioned)",
                Metadata = new Dictionary<string, string> { ["laplace_managed"] = "true" }
            }, cancellationToken: ct);
            await _config.SetAsync(WebhookSecretProvider.ConfigKey, created.Secret, ct);
            await _config.SetAsync(EndpointIdConfigKey, created.Id, ct);
            return new("created", created.Id, url);
        }
        catch (StripeException ex)
        {
            return new($"error:{ex.StripeError?.Code ?? "stripe_error"}", null, url);
        }
    }
}

internal sealed record BillingBootstrapResult(string StoreMode, bool StripeConfigured,
    bool BillingEnforced, StripeCatalogSyncResult Catalog, WebhookProvisionResult Webhook);
internal interface IBillingBootstrap
{
    Task<BillingBootstrapResult> RunAsync(CancellationToken ct);
}

internal sealed class BillingBootstrap : IBillingBootstrap
{
    private readonly IStripeCatalogSync _catalogSync;
    private readonly IStripeWebhookProvisioner _webhooks;
    private readonly StripeBillingOptions _options;
    private readonly BillingStoreMode _storeMode;
    public BillingBootstrap(IStripeCatalogSync catalogSync, IStripeWebhookProvisioner webhooks,
        IOptions<StripeBillingOptions> options, BillingStoreMode storeMode)
    {
        _catalogSync = catalogSync; _webhooks = webhooks; _options = options.Value; _storeMode = storeMode;
    }
    public async Task<BillingBootstrapResult> RunAsync(CancellationToken ct)
    {
        var catalog = await _catalogSync.EnsureAllAsync(ct);
        var webhook = await _webhooks.EnsureAsync(ct);
        return new(_storeMode.Mode, !string.IsNullOrWhiteSpace(_options.ApiKey), !_options.Bypass, catalog, webhook);
    }
}

internal sealed record BillingStoreMode(string Mode, string? Detail);
internal sealed class BillingBootstrapService : BackgroundService
{
    private readonly IBillingBootstrap _bootstrap;
    private readonly ILogger<BillingBootstrapService> _logger;
    public BillingBootstrapService(IBillingBootstrap bootstrap, ILogger<BillingBootstrapService> logger)
    {
        _bootstrap = bootstrap; _logger = logger;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await _bootstrap.RunAsync(stoppingToken);
            var synced = result.Catalog.Entries.Count(e => e.Status is "exists" or "created");
            _logger.LogInformation("billing bootstrap: store={Store} stripe={Stripe} enforced={Enforced} catalog={Synced}/{Total} webhook={Webhook}",
                result.StoreMode, result.StripeConfigured, result.BillingEnforced, synced, result.Catalog.Entries.Count, result.Webhook.Status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "billing bootstrap failed; serving continues, re-run via POST /v1/billing/operator/bootstrap");
        }
    }
}
