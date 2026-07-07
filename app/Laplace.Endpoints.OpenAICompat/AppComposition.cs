using Laplace.Chess.Service;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat;

internal static class AppComposition
{
    public static IServiceCollection AddOpenAiCompatServices(this IServiceCollection services)
    {
        services.AddSingleton<ITenantResolver, HeaderTenantResolver>();

        services.AddSingleton<SubstrateClient>();
        services.AddSingleton<ISubstrateClient>(sp => sp.GetRequiredService<SubstrateClient>());
        services.AddSingleton<ExploreDecomposeService>();
        services.AddSingleton<WitnessCatalog>(_ => WitnessCatalog.Load());
        services.AddSingleton<TurnWitness>();
        services.AddHostedService(sp => sp.GetRequiredService<TurnWitness>());

        const double chessWeight = 0.5d;
        services.AddSingleton(sp => new ChessEngineService(
            LaplaceInstall.PostgresConnectionString(), chessWeight,
            sp.GetService<ILoggerFactory>()?.CreateLogger("chess")));
        services.AddSingleton(sp => new ChessLabService(
            sp.GetService<ILoggerFactory>()?.CreateLogger("chess-lab")));

        services.AddSingleton<IBillingCatalog, StaticBillingCatalog>();
        services.AddSingleton<IStripeCatalogSync, StripeCatalogSync>();
        services.AddSingleton<ISynthesisQuoteCalculator, SynthesisQuoteCalculator>();
        services.AddSingleton<ITraceQuoteCalculator, TraceQuoteCalculator>();
        services.AddSingleton<IReportQuoteCalculator, ReportQuoteCalculator>();
        services.AddSingleton<IBillingWebhookHandler, BillingWebhookHandler>();
        services.AddSingleton<IStripeCheckoutGateway, StripeCheckoutGateway>();
        services.AddSingleton<IBillingOrchestrator, BillingOrchestrator>();

        services.AddSingleton<IStripePriceMap, InMemoryStripePriceMap>();
        services.AddSingleton<IBillingEntitlementStore, InMemoryBillingEntitlementStore>();
        services.AddSingleton<IBillingWebhookEventStore, InMemoryBillingWebhookEventStore>();
        services.AddSingleton<IBillingLedger, InMemoryBillingLedger>();
        services.AddSingleton<IBillingQuoteStore, InMemoryBillingQuoteStore>();

        services.AddOptions<StripeBillingOptions>().Configure(options =>
        {
            options.Currency = "usd";
            options.SuccessUrl = $"{LaplaceInstall.EndpointBaseUrl}/billing/success";
            options.CancelUrl = $"{LaplaceInstall.EndpointBaseUrl}/billing/cancel";
            options.Bypass = true;
        });

        return services;
    }

    public static string? ResolveQuoteId(HttpRequest request)
    {
        var header = request.Headers["X-Laplace-Quote-Id"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }
}
