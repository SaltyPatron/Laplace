using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// Factory for wire-shape goldens: deterministic fake substrate (no database) plus the
/// signed-webhook billing configuration, so quote approval flows run end to end in-memory.
/// </summary>
public sealed class GoldenFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISubstrateClient>();
            services.AddSingleton<ISubstrateClient, FakeSubstrateClient>();
            services.PostConfigure<StripeBillingOptions>(o =>
            {
                o.WebhookSecret = SignedWebhookFactory.WebhookSecret;
                o.SkipSignatureVerification = true;
            });
        });
}
