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
    // OpenAPI generation runs this host during compilation. It must neither
    // contact the payment database nor start writers or provision Stripe objects.
    private static bool IsOpenApiDocumentGeneration => string.Equals(
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
        "GetDocument.Insider", StringComparison.Ordinal);

    // The existing worker-suppression switch does not make a running API's
    // account database ephemeral. Only the actual schema generator does that.
    private static bool IsDocumentGenerationHost => IsOpenApiDocumentGeneration
        || Environment.GetEnvironmentVariable("LAPLACE_SKIP_HOSTED_SERVICES") == "1";

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
        var authMode = ResolveAuthMode(browserAuth);
        services.AddSingleton(browserAuth);
        var dataProtectionPath = IdentityConfig("LAPLACE_DATA_PROTECTION_KEYS");
        if (!string.IsNullOrWhiteSpace(dataProtectionPath))
        {
            if (!IsOpenApiDocumentGeneration) Directory.CreateDirectory(dataProtectionPath);
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
        services.AddSingleton<IStripeSubscriptionGateway, StripeSubscriptionGateway>();
        services.AddSingleton<IBillingWebhookHandler, BillingWebhookHandler>();
        services.AddSingleton<IStripeCheckoutGateway, StripeCheckoutGateway>();
        services.AddSingleton<IBillingOrchestrator, BillingOrchestrator>();
        AddBillingStores(services, requireDurable: authMode != "header");
        services.AddSingleton<IWebhookSecretProvider, WebhookSecretProvider>();
        services.AddSingleton<IStripeWebhookProvisioner, StripeWebhookProvisioner>();
        services.AddSingleton<IBillingBootstrap, BillingBootstrap>();
        services.AddServerHostedService<BillingBootstrapService>();
        services.AddSingleton<IApiKeyService, ApiKeyService>();

        services.AddOptions<LaplaceAuthOptions>().Configure(options =>
        {
            options.Mode = authMode;
            options.OperatorToken = FirstConfig(
                "LAPLACE_OPERATOR_TOKEN", "LAPLACE_OPERATOR_SECRET", secretFile: "stripe.env");
        });
        services.AddOptions<StripeBillingOptions>().Configure(options =>
        {
            options.ApiKey = FirstConfig(
                "STRIPE_API_SECRET", "LAPLACE_STRIPE_API_KEY", secretFile: "stripe.env");
            options.WebhookSecret = FirstConfig(
                "STRIPE_WEBHOOK_SECRET", "LAPLACE_STRIPE_WEBHOOK_SECRET", secretFile: "stripe.env");
            options.PublicBaseUrl = FirstConfig("LAPLACE_PUBLIC_BASE_URL");
            var externalBase = options.PublicBaseUrl?.TrimEnd('/') ?? LaplaceInstall.EndpointBaseUrl;
            options.Currency = FirstConfig("LAPLACE_BILLING_CURRENCY") ?? "usd";
            // The return page reads the signed-in workspace's provider session;
            // arriving at that page never grants an entitlement by itself.
            options.SuccessUrl = FirstConfig("LAPLACE_STRIPE_SUCCESS_URL")
                ?? $"{externalBase}/billing/success?session_id={{CHECKOUT_SESSION_ID}}";
            options.CancelUrl = FirstConfig("LAPLACE_STRIPE_CANCEL_URL")
                ?? $"{externalBase}/billing/cancel";
            options.Bypass = FirstConfig("LAPLACE_BILLING_BYPASS")?.ToLowerInvariant() switch
            {
                null => string.IsNullOrWhiteSpace(options.ApiKey),
                "true" or "1" => true,
                "false" or "0" => false,
                _ => throw new InvalidOperationException("LAPLACE_BILLING_BYPASS must be true, false, 1, or 0.")
            };
        });
        return services;
    }

    private static string ResolveAuthMode(BrowserAuthSettings browserAuth)
    {
        var configured = FirstConfig("LAPLACE_AUTH_MODE")?.ToLowerInvariant();
        if (configured is not null)
            return configured is "header" or "key" or "identity" ? configured
                : throw new InvalidOperationException("LAPLACE_AUTH_MODE must be identity, key, or the explicit development mode header.");
        var published = browserAuth.Providers.Count > 0
            || !string.IsNullOrWhiteSpace(FirstConfig("LAPLACE_PUBLIC_BASE_URL"))
            || !string.IsNullOrWhiteSpace(FirstConfig("STRIPE_API_SECRET", "LAPLACE_STRIPE_API_KEY", secretFile: "stripe.env"));
        return published ? "identity" : "header";
    }

    private static BrowserAuthSettings BuildBrowserAuthSettings()
    {
        var providers = new List<ExternalOidcProvider>();
        AddProvider(providers, "microsoft", "Microsoft",
            IdentityConfig("LAPLACE_AUTH_MICROSOFT_CLIENT_ID", "MICROSOFT_CLIENT_ID"),
            IdentityConfig("LAPLACE_AUTH_MICROSOFT_CLIENT_SECRET", "MICROSOFT_CLIENT_SECRET"),
            IdentityConfig("LAPLACE_AUTH_MICROSOFT_AUTHORITY") ?? "https://login.microsoftonline.com/common/v2.0",
            "/signin-microsoft");
        AddProvider(providers, "google", "Google",
            IdentityConfig("LAPLACE_AUTH_GOOGLE_CLIENT_ID", "GOOGLE_CLIENT_ID"),
            IdentityConfig("LAPLACE_AUTH_GOOGLE_CLIENT_SECRET", "GOOGLE_CLIENT_SECRET"),
            "https://accounts.google.com", "/signin-google");
        return new BrowserAuthSettings(providers);
    }

    private static void AddProvider(ICollection<ExternalOidcProvider> providers,
        string scheme, string displayName, string? clientId, string? clientSecret,
        string authority, string callbackPath)
    {
        if (string.IsNullOrWhiteSpace(clientId) && string.IsNullOrWhiteSpace(clientSecret)) return;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException($"{displayName} sign-in requires both its OAuth client id and client secret.");
        providers.Add(new ExternalOidcProvider(scheme, displayName, clientId, clientSecret, authority.TrimEnd('/'), callbackPath));
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

    // Authenticated company hosts cannot silently lose payments, keys and
    // allowances by falling back to an ephemeral store after a database error.
    private static void AddBillingStores(IServiceCollection services, bool requireDurable)
    {
        var requested = FirstConfig("LAPLACE_BILLING_STORE")?.ToLowerInvariant();
        if (requested is not (null or "auto" or "postgres" or "memory"))
            throw new InvalidOperationException("LAPLACE_BILLING_STORE must be postgres, memory, or auto.");
        string mode;
        string? detail = null;
        Npgsql.NpgsqlDataSource? dataSource = null;
        if (IsOpenApiDocumentGeneration)
        {
            mode = "memory";
            detail = "document_generation";
        }
        else if (requested is "memory")
        {
            if (requireDurable)
                throw new InvalidOperationException("Authenticated company deployments require LAPLACE_BILLING_STORE=postgres; memory is only for explicit header-mode development.");
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
            catch (Exception ex)
            {
                dataSource?.Dispose();
                dataSource = null;
                if (requireDurable || requested == "postgres") throw;
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

    private static string? FirstConfig(params string[] keys) => FirstConfig(keys, secretFile: null);
    private static string? FirstConfig(string key1, string key2, string? secretFile)
        => FirstConfig(new[] { key1, key2 }, secretFile);
    private static string? FirstConfig(string[] keys, string? secretFile)
    {
        foreach (var key in keys)
        {
            var value = LaplaceInstall.TryReadConfig(key, secretFile);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }
}
