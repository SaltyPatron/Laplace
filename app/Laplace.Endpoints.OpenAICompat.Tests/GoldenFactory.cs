using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class GoldenFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISubstrateClient>();
            services.AddSingleton<ISubstrateClient, FakeSubstrateClient>();

            services.RemoveAll<IHostedService>();
            services.RemoveAll<TurnWitness>();
            services.AddSingleton<TurnWitness>(sp =>
            {
                var witness = new TurnWitness(
                    sp.GetRequiredService<SubstrateClient>(),
                    sp.GetRequiredService<ILogger<TurnWitness>>());
                witness.TestForceAvailable = true;
                return witness;
            });

            services.PostConfigure<StripeBillingOptions>(o =>
            {
                o.Bypass = false;
                o.WebhookSecret = SignedWebhookFactory.WebhookSecret;
                o.SkipSignatureVerification = true;
            });
        });
}
