using Laplace.Endpoints.OpenAICompat.Auth;
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

            // The discovery contract is explicitly an unconfigured provider
            // host; installed OAuth registrations must not change its response.
            services.RemoveAll<BrowserAuthSettings>();
            services.AddSingleton(new BrowserAuthSettings([]));

            services.RemoveAll<IHostedService>();
            services.RemoveAll<IConversationWitness>();
            services.AddSingleton<IConversationWitness,RecordingConversationWitness>();

            services.PostConfigure<StripeBillingOptions>(o =>
            {
                // Goldens assert the unconfigured-Stripe wire shape. Host
                // deploy/secrets/stripe.env must not leak into the test host.
                TestBillingOptions.IsolateFromHostStripe(o);
                o.Bypass = false;
                o.WebhookSecret = SignedWebhookFactory.WebhookSecret;
                o.SkipSignatureVerification = true;
            });
        });
}
