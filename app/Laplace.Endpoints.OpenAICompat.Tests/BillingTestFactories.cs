using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Laplace.Endpoints.OpenAICompat.Tests;

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
            services.PostConfigure<StripeBillingOptions>(o =>
            {
                o.WebhookSecret = SignedWebhookFactory.WebhookSecret;
                o.SkipSignatureVerification = false;
            }));
}

internal sealed class UnconfiguredWebhookFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
            services.PostConfigure<StripeBillingOptions>(o => o.WebhookSecret = null));
}
