using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Endpoints.OpenAICompat.BillingPostgres;
using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat;

internal static class AppComposition
{
    public static IServiceCollection AddOpenAiCompatServices(this IServiceCollection services)
    {
        
        
        var authMode = Environment.GetEnvironmentVariable("LAPLACE_AUTH_MODE") ?? "header";
        services.AddSingleton<ITenantResolver>(authMode.ToLowerInvariant() switch
        {
            "header" => new HeaderTenantResolver(),
            _ => throw new InvalidOperationException(
                $"Unknown LAPLACE_AUTH_MODE '{authMode}' (supported: header).")
        });

        services.AddSingleton<SubstrateClient>();
        services.AddSingleton<ISubstrateClient>(sp => sp.GetRequiredService<SubstrateClient>());
        services.AddSingleton<TurnWitness>();
        services.AddHostedService(sp => sp.GetRequiredService<TurnWitness>());
        services.AddSingleton<IBillingCatalog, StaticBillingCatalog>();
        services.AddSingleton<IStripeCatalogSync, StripeCatalogSync>();
        services.AddSingleton<ISynthesisQuoteCalculator, SynthesisQuoteCalculator>();
        services.AddSingleton<ITraceQuoteCalculator, TraceQuoteCalculator>();
        services.AddSingleton<IReportQuoteCalculator, ReportQuoteCalculator>();
        services.AddSingleton<IBillingWebhookHandler, BillingWebhookHandler>();
        services.AddSingleton<IStripeCheckoutGateway, StripeCheckoutGateway>();
        services.AddSingleton<IBillingOrchestrator, BillingOrchestrator>();

        
        
        
        var billingStore = Environment.GetEnvironmentVariable("LAPLACE_BILLING_STORE") ?? "memory";
        if (string.Equals(billingStore, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IStripePriceMap>(sp => new PostgresStripePriceMap(sp.GetRequiredService<SubstrateClient>().DataSource));
            services.AddSingleton<IBillingEntitlementStore>(sp => new PostgresBillingEntitlementStore(sp.GetRequiredService<SubstrateClient>().DataSource));
            services.AddSingleton<IBillingWebhookEventStore>(sp => new PostgresBillingWebhookEventStore(sp.GetRequiredService<SubstrateClient>().DataSource));
            services.AddSingleton<IBillingLedger>(sp => new PostgresBillingLedger(sp.GetRequiredService<SubstrateClient>().DataSource));
            services.AddSingleton<IBillingQuoteStore>(sp => new PostgresBillingQuoteStore(sp.GetRequiredService<SubstrateClient>().DataSource));
        }
        else if (string.Equals(billingStore, "memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IStripePriceMap, InMemoryStripePriceMap>();
            services.AddSingleton<IBillingEntitlementStore, InMemoryBillingEntitlementStore>();
            services.AddSingleton<IBillingWebhookEventStore, InMemoryBillingWebhookEventStore>();
            services.AddSingleton<IBillingLedger, InMemoryBillingLedger>();
            services.AddSingleton<IBillingQuoteStore, InMemoryBillingQuoteStore>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown LAPLACE_BILLING_STORE '{billingStore}' (supported: memory, postgres).");
        }

        services.AddOptions<StripeBillingOptions>().Configure(options =>
        {
            options.ApiKey = Environment.GetEnvironmentVariable("LAPLACE_STRIPE_API_KEY");
            options.WebhookSecret = Environment.GetEnvironmentVariable("LAPLACE_STRIPE_WEBHOOK_SECRET");
            options.SuccessUrl = Environment.GetEnvironmentVariable("LAPLACE_STRIPE_SUCCESS_URL") ?? "http://localhost:5187/billing/success";
            options.CancelUrl = Environment.GetEnvironmentVariable("LAPLACE_STRIPE_CANCEL_URL") ?? "http://localhost:5187/billing/cancel";
            options.Currency = Environment.GetEnvironmentVariable("LAPLACE_BILLING_CURRENCY") ?? "usd";
            options.Bypass   = string.Equals(Environment.GetEnvironmentVariable("LAPLACE_BILLING_BYPASS"), "true", StringComparison.OrdinalIgnoreCase);
        });

        return services;
    }

    public static string? ResolveQuoteId(HttpRequest request)
    {
        var header = request.Headers["X-Laplace-Quote-Id"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }
}
