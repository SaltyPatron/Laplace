using Laplace.Chess.Service;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Laplace.Endpoints.OpenAICompat;

internal static class AppComposition
{
    /// <summary>
    /// True when the process is a build-time OpenAPI document generator rather than a
    /// server.
    ///
    /// `Microsoft.Extensions.ApiDescription.Server` runs the application's host inside
    /// `GetDocument.Insider` during the BUILD to emit web/openapi/openapi.json. That
    /// starts every IHostedService. MEASURED 2026-08-10 in a CI build log:
    ///
    ///   GenerateOpenApiDocuments:
    ///     [INF] TurnWitness: turn-witness online
    ///     [WRN] CatalogPrewarmService: explore catalog prewarm failed
    ///     Npgsql.PostgresException 3D000: database "laplace" does not exist
    ///
    /// A compile brought up the substrate WRITER (TurnWitness), opened Postgres
    /// (CatalogPrewarm), and started BillingBootstrapService -- whose documented job is
    /// to self-provision the Stripe catalog, prices and webhooks idempotently. The only
    /// reason that build did not reach a database or a payment processor is that the
    /// environment was broken at the time. Nothing in the code prevented it.
    ///
    /// The repo already knew the remedy and applied it in exactly two places, both
    /// tests -- GoldenFactory.cs:19 and BillingIdentityTests.cs:50 both call
    /// `services.RemoveAll&lt;IHostedService&gt;()`. .scratchpad/31 flagged the same shape
    /// for BillingTestFactories and named GoldenFactory as the fix. It was applied to
    /// the test factories and never to the composition, so the production path still
    /// boots everything for a schema dump.
    ///
    /// Guarding at REGISTRATION rather than asking each entry point to strip services
    /// afterwards: a new host inherits the guard, and a new hosted service is covered
    /// the day it is added. The document generator only needs the endpoint surface --
    /// routes, DTOs, auth metadata -- all of which are registered below this line.
    /// </summary>
    private static bool IsDocumentGenerationHost =>
        string.Equals(
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
            "GetDocument.Insider",
            StringComparison.Ordinal)
        || Environment.GetEnvironmentVariable("LAPLACE_SKIP_HOSTED_SERVICES") == "1";

    /// <summary>
    /// Registers a hosted service unless this process is a build-time document
    /// generator. Every AddHostedService in this file goes through here; a bare
    /// AddHostedService call is the regression.
    /// </summary>
    private static IServiceCollection AddServerHostedService<T>(this IServiceCollection services)
        where T : class, IHostedService
    {
        if (IsDocumentGenerationHost) return services;
        return services.AddHostedService<T>();
    }

    private static IServiceCollection AddServerHostedService<T>(
        this IServiceCollection services, Func<IServiceProvider, T> factory)
        where T : class, IHostedService
    {
        if (IsDocumentGenerationHost) return services;
        return services.AddHostedService(factory);
    }
    public static IServiceCollection AddOpenAiCompatServices(this IServiceCollection services)
    {
        var browserAuth = BuildBrowserAuthSettings();
        services.AddSingleton(browserAuth);
        var dataProtectionPath = IdentityConfig("LAPLACE_DATA_PROTECTION_KEYS");
        if (!string.IsNullOrWhiteSpace(dataProtectionPath))
        {
            Directory.CreateDirectory(dataProtectionPath);
            services.AddDataProtection()
                .SetApplicationName("Laplace")
                .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
        }
        services.AddSingleton<IIdentityStore>(sp =>
            new PostgresIdentityStore(sp.GetRequiredService<SubstrateClient>().DataSource));
        services.AddSingleton<BrowserTicketStore>();

        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = BrowserAuthSettings.CookieScheme;
            options.DefaultSignInScheme = BrowserAuthSettings.CookieScheme;
        }).AddCookie(BrowserAuthSettings.CookieScheme, options =>
        {
            options.Cookie.Name = "__Host-laplace-session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
        });
        foreach (var provider in browserAuth.Providers)
        {
            authentication.AddOpenIdConnect(provider.Scheme, provider.DisplayName,
                options => BrowserAuthentication.Configure(options, provider));
        }
        services.AddOptions<CookieAuthenticationOptions>(BrowserAuthSettings.CookieScheme)
            .Configure<BrowserTicketStore>((options, tickets) => options.SessionStore = tickets);

        services.AddSingleton<ITenantResolver, ApiKeyTenantResolver>();

        services.AddSingleton<SubstrateClient>();
        services.AddSingleton<ISubstrateClient>(sp => sp.GetRequiredService<SubstrateClient>());
        services.AddSingleton<ExploreDecomposeService>();
        services.AddSingleton<WitnessCatalog>(_ => WitnessCatalog.Load());
        services.AddSingleton<TurnWitness>();
        services.AddSingleton<IConversationWitness>(sp => sp.GetRequiredService<TurnWitness>());
        services.AddServerHostedService(sp => sp.GetRequiredService<TurnWitness>());
        services.AddServerHostedService<CatalogPrewarmService>();
        if (browserAuth.Providers.Count > 0)
            services.AddServerHostedService<IdentityClientBootstrapService>();

        const double chessWeight = 0.5d;
        services.AddSingleton(sp => new ChessRuntimeService(
            sp.GetRequiredService<ILogger<ChessRuntimeService>>(), chessWeight));
        services.AddServerHostedService(sp => sp.GetRequiredService<ChessRuntimeService>());
        services.AddSingleton(sp => new ChessEngineService(
            chessWeight,
            sp.GetRequiredService<SubstrateClient>().ChessReadOnlyDataSource,
            sp.GetRequiredService<ChessRuntimeService>().GetAsync,
            sp.GetService<ILoggerFactory>()?.CreateLogger("chess")));
        services.AddSingleton(sp => new ChessLabService(
            sp.GetRequiredService<ChessRuntimeService>().GetAsync,
            sp.GetService<ILoggerFactory>()?.CreateLogger("chess-lab")));
        services.AddHttpClient<ILichessStatusClient, LichessStatusClient>(client =>
        {
            client.BaseAddress = new Uri("http://127.0.0.1:5189");
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<IServiceControl, ServiceControl>();

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
        services.AddServerHostedService<BillingBootstrapService>();
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

    private static BrowserAuthSettings BuildBrowserAuthSettings()
    {
        var providers = new List<ExternalOidcProvider>();
        AddProvider(
            providers,
            scheme: "microsoft",
            displayName: "Microsoft",
            clientId: IdentityConfig("LAPLACE_AUTH_MICROSOFT_CLIENT_ID", "MICROSOFT_CLIENT_ID"),
            clientSecret: IdentityConfig("LAPLACE_AUTH_MICROSOFT_CLIENT_SECRET", "MICROSOFT_CLIENT_SECRET"),
            authority: IdentityConfig("LAPLACE_AUTH_MICROSOFT_AUTHORITY")
                ?? "https://login.microsoftonline.com/common/v2.0",
            callbackPath: "/signin-microsoft");
        AddProvider(
            providers,
            scheme: "google",
            displayName: "Google",
            clientId: IdentityConfig("LAPLACE_AUTH_GOOGLE_CLIENT_ID", "GOOGLE_CLIENT_ID"),
            clientSecret: IdentityConfig("LAPLACE_AUTH_GOOGLE_CLIENT_SECRET", "GOOGLE_CLIENT_SECRET"),
            authority: "https://accounts.google.com",
            callbackPath: "/signin-google");
        return new BrowserAuthSettings(providers);
    }

    private static void AddProvider(
        ICollection<ExternalOidcProvider> providers,
        string scheme,
        string displayName,
        string? clientId,
        string? clientSecret,
        string authority,
        string callbackPath)
    {
        if (string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(clientSecret))
            return;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException(
                $"{displayName} sign-in requires both its OAuth client id and client secret.");
        providers.Add(new ExternalOidcProvider(
            scheme, displayName, clientId, clientSecret, authority.TrimEnd('/'), callbackPath));
    }

    private static string? IdentityConfig(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = LaplaceInstall.TryReadConfig(key, "identity.env");
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
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
                dataSource = LaplaceDataSource.Create(SubstrateAccess.Serving);
                BillingPostgres.BillingSchemaProbe.EnsureQuotesTableReachable(dataSource);
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
