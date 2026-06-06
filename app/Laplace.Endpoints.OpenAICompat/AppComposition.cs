using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat;

internal static class AppComposition
{
    public static IServiceCollection AddOpenAiCompatServices(this IServiceCollection services)
    {
        services.AddSingleton<SubstrateClient>();
        services.AddSingleton<IBillingCatalog, StaticBillingCatalog>();
        services.AddSingleton<IStripePriceMap, InMemoryStripePriceMap>();
        services.AddSingleton<IStripeCatalogSync, StripeCatalogSync>();
        services.AddSingleton<ISynthesisQuoteCalculator, SynthesisQuoteCalculator>();
        services.AddSingleton<ITraceQuoteCalculator, TraceQuoteCalculator>();
        services.AddSingleton<IReportQuoteCalculator, ReportQuoteCalculator>();
        services.AddSingleton<IBillingEntitlementStore, InMemoryBillingEntitlementStore>();
        services.AddSingleton<IBillingWebhookEventStore, InMemoryBillingWebhookEventStore>();
        services.AddSingleton<IBillingWebhookHandler, BillingWebhookHandler>();
        services.AddSingleton<IBillingLedger, InMemoryBillingLedger>();
        services.AddSingleton<IBillingQuoteStore, InMemoryBillingQuoteStore>();
        services.AddSingleton<IStripeCheckoutGateway, StripeCheckoutGateway>();
        services.AddSingleton<IBillingOrchestrator, BillingOrchestrator>();

        services.AddOptions<StripeBillingOptions>().Configure(options =>
        {
            options.ApiKey = Environment.GetEnvironmentVariable("LAPLACE_STRIPE_API_KEY");
            options.WebhookSecret = Environment.GetEnvironmentVariable("LAPLACE_STRIPE_WEBHOOK_SECRET");
            options.SuccessUrl = Environment.GetEnvironmentVariable("LAPLACE_STRIPE_SUCCESS_URL") ?? "http://localhost:5187/billing/success";
            options.CancelUrl = Environment.GetEnvironmentVariable("LAPLACE_STRIPE_CANCEL_URL") ?? "http://localhost:5187/billing/cancel";
            options.Currency = Environment.GetEnvironmentVariable("LAPLACE_BILLING_CURRENCY") ?? "usd";
        });

        return services;
    }

    public static string ResolveTenant(HttpRequest request)
    {
        var header = request.Headers["X-Laplace-Tenant"].ToString();
        return string.IsNullOrWhiteSpace(header) ? "local-dev" : header.Trim();
    }

    public static string? ResolveQuoteId(HttpRequest request)
    {
        var header = request.Headers["X-Laplace-Quote-Id"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }
}
