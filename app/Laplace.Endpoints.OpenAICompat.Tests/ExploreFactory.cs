using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class ExploreFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISubstrateClient>();
            services.AddSingleton<ISubstrateClient, FakeSubstrateClient>();
            services.PostConfigure<StripeBillingOptions>(o =>
            {
                TestBillingOptions.IsolateFromHostStripe(o);
                o.WebhookSecret = SignedWebhookFactory.WebhookSecret;
                o.SkipSignatureVerification = true;
                o.Bypass = true;
            });
        });
}
