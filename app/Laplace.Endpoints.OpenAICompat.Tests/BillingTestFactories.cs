using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class SignedWebhookFactory : WebApplicationFactory<Program>
{
    public const string WebhookSecret = "whsec_laplace_ci_test_secret";

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
            services.PostConfigure<StripeBillingOptions>(o => o.WebhookSecret = WebhookSecret));

    public static string Sign(string payload, DateTimeOffset? at = null)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexString(signature).ToLowerInvariant()}";
    }
}

internal sealed class UnconfiguredWebhookFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
            services.PostConfigure<StripeBillingOptions>(o => o.WebhookSecret = null));
}
