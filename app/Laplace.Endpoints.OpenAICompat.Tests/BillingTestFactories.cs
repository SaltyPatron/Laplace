using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
            services.PostConfigure<StripeBillingOptions>(o =>
            {
                TestBillingOptions.IsolateFromHostStripe(o);
                o.Bypass = false;
                o.WebhookSecret = WebhookSecret;
                o.SkipSignatureVerification = true;
            });
        });

    public static string Sign(string payload, DateTimeOffset? at = null)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return $"t={timestamp},v1=test";
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
