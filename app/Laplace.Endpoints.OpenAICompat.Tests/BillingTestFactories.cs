using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stripe;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// AppComposition loads STRIPE_* from process env / deploy/secrets. Contract and
/// golden tests must not call live Stripe or inherit host checkout URLs / price ids.
/// </summary>
internal static class TestBillingOptions
{
    public static void IsolateFromHostStripe(StripeBillingOptions o)
    {
        o.ApiKey = null;
    }
}

public sealed class SignedWebhookFactory : WebApplicationFactory<Program>
{
    public const string WebhookSecret = "whsec_laplace_ci_test_secret";

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISubstrateClient>();
            services.AddSingleton<ISubstrateClient, UnreachableSubstrateClient>();
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            TestStripeSubscriptions.Configure(services);
            services.PostConfigure<StripeBillingOptions>(o =>
            {
                TestBillingOptions.IsolateFromHostStripe(o);
                o.Bypass = false;
                o.WebhookSecret = WebhookSecret;
                o.SkipSignatureVerification = false;
            });
        });

    public static string Sign(string payload, DateTimeOffset? at = null)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var signedBytes = Encoding.UTF8.GetBytes($"{timestamp}.{payload}");
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), signedBytes);
        return $"t={timestamp},v1={Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}

internal sealed class StrictWebhookFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            // Webhook-path tests never touch the substrate: without these the
            // factory booted the production composition — a real
            // NpgsqlDataSource plus CatalogPrewarmService firing the explore
            // catalog load against whatever DB the runner .env points at.
            services.RemoveAll<ISubstrateClient>();
            services.AddSingleton<ISubstrateClient, UnreachableSubstrateClient>();
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            services.PostConfigure<StripeBillingOptions>(o =>
            {
                TestBillingOptions.IsolateFromHostStripe(o);
                o.WebhookSecret = SignedWebhookFactory.WebhookSecret;
                o.SkipSignatureVerification = false;
            });
        });
}

internal sealed class UnconfiguredWebhookFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISubstrateClient>();
            services.AddSingleton<ISubstrateClient, UnreachableSubstrateClient>();
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
            services.PostConfigure<StripeBillingOptions>(o =>
            {
                TestBillingOptions.IsolateFromHostStripe(o);
                o.WebhookSecret = null;
            });
        });
}

internal static class WebhookTestEvents
{
    // Positive checkout fixtures must establish the same persisted binding that
    // a successful checkout provider creates. No handler or approval is replaced.
    public static async Task BindCheckoutAsync(IServiceProvider services, string quoteId, string sessionId)
    {
        var store = services.GetRequiredService<IBillingQuoteStore>();
        var quote = await store.TryGetAsync(quoteId, CancellationToken.None);
        Assert.NotNull(quote);
        await store.UpdateAsync(quote with { StripeSessionId = sessionId, Status = "pending_payment" }, CancellationToken.None);
    }

    public static string PaidCheckout(string eventId, string tenant, string serviceId, string quoteId,
        string sessionId, string? subscriptionId = null, string? customerId = null) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    @object = "checkout.session",
                    id = sessionId,
                    payment_status = "paid",
                    customer = customerId ?? $"cus_{eventId}",
                    subscription = subscriptionId,
                    metadata = new { tenant, service_id = serviceId, quote_id = quoteId }
                }
            }
        });

    public static string Subscription(string eventId, string eventType, string subscriptionId, long created) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            created,
            type = eventType,
            data = new { @object = new { @object = "subscription", id = subscriptionId } }
        });
}

// Only the external provider read is controlled. The production webhook handler,
// quote binding, event deduplication, period handling and credit store all execute.
internal sealed class TestStripeSubscriptions : IStripeSubscriptionGateway
{
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);

    public static void Configure(IServiceCollection services)
    {
        services.RemoveAll<IStripeSubscriptionGateway>();
        services.AddSingleton<TestStripeSubscriptions>();
        services.AddSingleton<IStripeSubscriptionGateway>(provider => provider.GetRequiredService<TestStripeSubscriptions>());
    }

    public void Set(IServiceProvider services, string subscriptionId, string tenant, string serviceId,
        string customerId, DateTimeOffset start, DateTimeOffset end, string status = "active")
    {
        Assert.True(end > start);
        var catalog = services.GetRequiredService<IBillingCatalog>();
        Assert.True(catalog.TryGet(serviceId, out var price));
        _subscriptions[subscriptionId] = new Subscription
        {
            Id = subscriptionId,
            CustomerId = customerId,
            Status = status,
            Metadata = new() { ["tenant"] = tenant },
            Items = new StripeList<SubscriptionItem>
            {
                Data = [new SubscriptionItem
                {
                    Id = $"si_{subscriptionId}",
                    CurrentPeriodStart = start.UtcDateTime,
                    CurrentPeriodEnd = end.UtcDateTime,
                    Price = new Price { LookupKey = price.LookupKey }
                }]
            }
        };
    }

    public int Reads(string subscriptionId) => _reads.GetValueOrDefault(subscriptionId);

    public Task<Subscription> GetAsync(string subscriptionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _reads.AddOrUpdate(subscriptionId, 1, (_, count) => count + 1);
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
            throw new InvalidOperationException($"No provider subscription fixture for {subscriptionId}.");
        return Task.FromResult(subscription);
    }
}
