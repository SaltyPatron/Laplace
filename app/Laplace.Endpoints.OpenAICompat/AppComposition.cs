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
        services.AddHostedService<CatalogPrewarmService>();

        const double chessWeight = 0.5d;
        services.AddSingleton(_ =>
            ChessLiveGameHost.CreateAsync(chessWeight).ConfigureAwait(false).GetAwaiter().GetResult());
        services.AddSingleton(sp => new ChessEngineService(
            LaplaceInstall.PostgresConnectionString(), chessWeight,
            sp.GetRequiredService<ChessLiveGameHost>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("chess")));
        services.AddSingleton(sp => new ChessLabService(
            sp.GetService<ILoggerFactory>()?.CreateLogger("chess-lab")));
        services.AddSingleton(sp => new LichessConnectivityService(
            sp.GetRequiredService<ChessLiveGameHost>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("lichess")));

        services.AddSingleton<IRecipeCompileService, RecipeCompileService>();
        services.AddSingleton<IFoundryExportService, CliFoundryExportService>();

        Laplace.Decomposers.Composition.SeedIngestComposition.AddLaplaceSeedIngest(services);

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
            // Prefer operator names (repo .env / secrets.env): STRIPE_API_SECRET.
            // LAPLACE_STRIPE_* kept as fallback for older runner bootstrap blocks.
            options.ApiKey = FirstConfig(
                "STRIPE_API_SECRET", "LAPLACE_STRIPE_API_KEY", secretFile: "stripe.env");
            options.WebhookSecret = FirstConfig(
                "STRIPE_WEBHOOK_SECRET", "LAPLACE_STRIPE_WEBHOOK_SECRET", secretFile: "stripe.env");

            options.Currency = FirstConfig("LAPLACE_BILLING_CURRENCY") ?? "usd";
            options.SuccessUrl = FirstConfig("LAPLACE_STRIPE_SUCCESS_URL")
                ?? $"{LaplaceInstall.EndpointBaseUrl}/billing/success";
            options.CancelUrl = FirstConfig("LAPLACE_STRIPE_CANCEL_URL")
                ?? $"{LaplaceInstall.EndpointBaseUrl}/billing/cancel";
            // LAPLACE_BILLING_BYPASS was set by e2e-web.cmd and stripped by test-app.cmd
            // while the code hardcoded `true` and read the env nowhere — the scripts were
            // toggling a placebo. Honor it: default stays true (local dev auto-unlocks the
            // quote gates); deploys and gated test runs set it to false explicitly.
            options.Bypass = !string.Equals(
                Environment.GetEnvironmentVariable("LAPLACE_BILLING_BYPASS"),
                "false", StringComparison.OrdinalIgnoreCase);
        });

        return services;
    }

    public static string? ResolveQuoteId(HttpRequest request)
    {
        var header = request.Headers["X-Laplace-Quote-Id"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }

    /// <summary>Process env, then <c>deploy/secrets/{secretFile}</c>, first non-empty key wins.</summary>
    private static string? FirstConfig(params string[] keys) => FirstConfig(keys, secretFile: null);

    private static string? FirstConfig(string key1, string key2, string? secretFile)
        => FirstConfig(new[] { key1, key2 }, secretFile);

    private static string? FirstConfig(string[] keys, string? secretFile)
    {
        foreach (var key in keys)
        {
            var value = LaplaceInstall.TryReadConfig(key, secretFile);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}
