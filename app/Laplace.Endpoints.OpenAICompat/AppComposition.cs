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
        services.AddSingleton<ITenantResolver, ApiKeyTenantResolver>();

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

        AddBillingStores(services);

        services.AddSingleton<IWebhookSecretProvider, WebhookSecretProvider>();
        services.AddSingleton<IStripeWebhookProvisioner, StripeWebhookProvisioner>();
        services.AddSingleton<IBillingBootstrap, BillingBootstrap>();
        services.AddHostedService<BillingBootstrapService>();
        services.AddSingleton<IApiKeyService, ApiKeyService>();

        services.AddOptions<LaplaceAuthOptions>().Configure(options =>
        {
            options.Mode = FirstConfig("LAPLACE_AUTH_MODE") ?? "header";
            options.OperatorToken = FirstConfig(
                "LAPLACE_OPERATOR_TOKEN", "LAPLACE_OPERATOR_SECRET", secretFile: "stripe.env");
        });

        services.AddOptions<StripeBillingOptions>().Configure(options =>
        {
            // Prefer operator names (repo .env / secrets.env): STRIPE_API_SECRET.
            // LAPLACE_STRIPE_* kept as fallback for older runner bootstrap blocks.
            options.ApiKey = FirstConfig(
                "STRIPE_API_SECRET", "LAPLACE_STRIPE_API_KEY", secretFile: "stripe.env");
            options.WebhookSecret = FirstConfig(
                "STRIPE_WEBHOOK_SECRET", "LAPLACE_STRIPE_WEBHOOK_SECRET", secretFile: "stripe.env");

            options.PublicBaseUrl = FirstConfig("LAPLACE_PUBLIC_BASE_URL");
            var externalBase = options.PublicBaseUrl?.TrimEnd('/') ?? LaplaceInstall.EndpointBaseUrl;

            options.Currency = FirstConfig("LAPLACE_BILLING_CURRENCY") ?? "usd";
            // {CHECKOUT_SESSION_ID} is substituted by Stripe on redirect; the SPA's
            // success page hands it to POST /v1/billing/keys/redeem for key issuance.
            options.SuccessUrl = FirstConfig("LAPLACE_STRIPE_SUCCESS_URL")
                ?? $"{externalBase}/billing/success?session_id={{CHECKOUT_SESSION_ID}}";
            options.CancelUrl = FirstConfig("LAPLACE_STRIPE_CANCEL_URL")
                ?? $"{externalBase}/billing/cancel";
            // Explicit LAPLACE_BILLING_BYPASS always wins. Unset means: enforce billing
            // exactly when Stripe is configured — a fresh install with a Stripe key
            // charges out of the box; a keyless local checkout stays unlocked.
            var bypassEnv = Environment.GetEnvironmentVariable("LAPLACE_BILLING_BYPASS");
            options.Bypass = string.IsNullOrWhiteSpace(bypassEnv)
                ? string.IsNullOrWhiteSpace(options.ApiKey)
                : !string.Equals(bypassEnv, "false", StringComparison.OrdinalIgnoreCase);
        });

        return services;
    }

    /// <summary>
    /// LAPLACE_BILLING_STORE: "postgres" | "memory" | unset (auto). Auto probes the
    /// app billing tables and prefers Postgres so paid quotes, plan credits, usage,
    /// and API keys survive deploys; "memory" remains for tests/ephemeral runs.
    /// </summary>
    private static void AddBillingStores(IServiceCollection services)
    {
        var requested = FirstConfig("LAPLACE_BILLING_STORE")?.ToLowerInvariant();
        string mode;
        string? detail = null;
        Npgsql.NpgsqlDataSource? dataSource = null;

        if (requested is "memory")
        {
            mode = "memory";
            detail = "explicit";
        }
        else
        {
            try
            {
                dataSource = new Npgsql.NpgsqlDataSourceBuilder(
                    LaplaceInstall.PostgresConnectionString()).Build();
                using var conn = dataSource.OpenConnection();
                using var cmd = new Npgsql.NpgsqlCommand("SELECT 1 FROM app.billing_quotes LIMIT 1;", conn);
                cmd.ExecuteNonQuery();
                mode = "postgres";
            }
            catch (Exception ex) when (requested is not "postgres")
            {
                dataSource?.Dispose();
                dataSource = null;
                mode = "memory";
                detail = $"auto_fallback:{ex.GetType().Name}";
            }
        }

        services.AddSingleton(new BillingStoreMode(mode, detail));

        if (dataSource is not null)
        {
            var ds = dataSource;
            services.AddSingleton<IStripePriceMap>(new BillingPostgres.PostgresStripePriceMap(ds));
            services.AddSingleton<IBillingEntitlementStore>(new BillingPostgres.PostgresBillingEntitlementStore(ds));
            services.AddSingleton<IBillingWebhookEventStore>(new BillingPostgres.PostgresBillingWebhookEventStore(ds));
            services.AddSingleton<IBillingLedger>(new BillingPostgres.PostgresBillingLedger(ds));
            services.AddSingleton<IBillingQuoteStore>(new BillingPostgres.PostgresBillingQuoteStore(ds));
            services.AddSingleton<IBillingConfigStore>(new PostgresBillingConfigStore(ds));
            services.AddSingleton<IApiKeyStore>(new PostgresApiKeyStore(ds));
        }
        else
        {
            services.AddSingleton<IStripePriceMap, InMemoryStripePriceMap>();
            services.AddSingleton<IBillingEntitlementStore, InMemoryBillingEntitlementStore>();
            services.AddSingleton<IBillingWebhookEventStore, InMemoryBillingWebhookEventStore>();
            services.AddSingleton<IBillingLedger, InMemoryBillingLedger>();
            services.AddSingleton<IBillingQuoteStore, InMemoryBillingQuoteStore>();
            services.AddSingleton<IBillingConfigStore, InMemoryBillingConfigStore>();
            services.AddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
        }
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
