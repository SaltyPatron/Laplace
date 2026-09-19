using System.Net;
using System.Threading.RateLimiting;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Laplace.Endpoints.OpenAICompat;
using Laplace.Ops;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = LaplaceInstall.InstallRoot,
    WebRootPath = LaplaceInstall.WebRoot,
});

builder.WebHost.ConfigureKestrel(options =>
    options.ListenLocalhost(
        int.TryParse(Environment.GetEnvironmentVariable("LAPLACE_API_PORT"), out var devPort)
            ? devPort
            : LaplaceInstall.EndpointPort));

builder.Host.UseSerilog((_, lc) =>
    lc.MinimumLevel.Information().ApplyLaplaceSinks("api", console: true, consoleToStdErr: false));

builder.Services.AddOpenAiCompatServices();
builder.Services.AddSingleton<AdminPostgresDataSources>();
builder.Services.AddSingleton(sp => new ContentArtifactCloser(
    sp.GetRequiredService<SubstrateClient>().DataSource,
    message => sp.GetRequiredService<ILogger<ContentArtifactCloser>>().LogWarning("{Message}", message)));
builder.Services.AddOpenApi();

const int perClientPerMinute = 300;
const int webhookPerMinute = 120;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddConcurrencyLimiter("public-query", limiter =>
    {
        limiter.PermitLimit = 16;
        limiter.QueueLimit = 0;
    });
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetNoLimiter("exempt");

        if (path.StartsWith("/v1/billing/webhooks", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetFixedWindowLimiter(
                $"webhook:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = webhookPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });

        var partition = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetSlidingWindowLimiter($"client:{partition}",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = perClientPerMinute,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
    });
});

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter())
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

var app = builder.Build();

var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
forwardedHeaders.KnownProxies.Add(IPAddress.Loopback);
forwardedHeaders.KnownProxies.Add(IPAddress.IPv6Loopback);
app.UseForwardedHeaders(forwardedHeaders);

// Published identity and billing links have one configured authority. Apply it
// before authentication constructs an OIDC callback so a reverse proxy cannot
// lose a non-default external port (for example :8443) from the redirect URI.
var publicBaseUrl = Environment.GetEnvironmentVariable("LAPLACE_PUBLIC_BASE_URL")?.TrimEnd('/');
if (!string.IsNullOrWhiteSpace(publicBaseUrl))
{
    if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicOrigin)
        || publicOrigin.Scheme != Uri.UriSchemeHttps
        || publicOrigin.AbsolutePath != "/"
        || !string.IsNullOrEmpty(publicOrigin.Query)
        || !string.IsNullOrEmpty(publicOrigin.Fragment))
        throw new InvalidOperationException(
            "LAPLACE_PUBLIC_BASE_URL must be an HTTPS origin without a path, query, or fragment.");

    var publicHost = publicOrigin.IsDefaultPort
        ? new HostString(publicOrigin.Host)
        : new HostString(publicOrigin.Host, publicOrigin.Port);
    app.Use((context, next) =>
    {
        // ForwardedHeaders has already established whether the reverse proxy
        // received HTTPS. Never promote a direct plaintext request merely
        // because a public origin is configured.
        if (context.Request.IsHttps)
        {
            context.Request.Scheme = publicOrigin.Scheme;
            if (context.Request.Host != publicHost)
                context.Request.Host = publicHost;
        }
        return next(context);
    });
}
app.UseMiddleware<RefactorProxyMiddleware>();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl =
            ctx.Context.Request.Path.StartsWithSegments("/assets")
                ? "public, max-age=31536000, immutable"
                : "no-cache";
    }
});

app.UseRateLimiter();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionEnvelopeMiddleware>();
app.UseAuthentication();
app.UseMiddleware<Laplace.Endpoints.OpenAICompat.Auth.ApiKeyEnforcementMiddleware>();
app.UseMiddleware<Laplace.Endpoints.OpenAICompat.Auth.BillingAccountBoundaryMiddleware>();
// Exact-model dispatch belongs before the generic OpenAI endpoint. A code request
// can never drift into the prose walk simply because both share the same URL.
app.UseMiddleware<CodeModelChatMiddleware>();

app.MapPrometheusScrapingEndpoint();
app.MapOpenApi();
app.MapCoreEndpoints();
app.MapQueryEndpoints();
app.MapOpEndpoints();
app.MapAdminEndpoints();
app.MapIngestAdminEndpoints();
app.MapServiceControlEndpoints();
app.MapOpenAiCompatEndpoints();
app.MapCodeEndpoints();
app.MapFoundryEndpoints();
app.MapBillingEndpoints();
app.MapBillingIdentityEndpoints();
app.MapIdentityEndpoints();
app.MapAccountEndpoints();
app.MapChessEndpoints();
app.MapChessPlayerModelEndpoints();
app.MapChessReadEndpoints();
app.MapFeedbackEndpoints();
app.MapUserContentEndpoints();

app.MapFallback("/v1/{*path}", () => Results.Json(
    new Laplace.Api.Contracts.ErrorResponse(
        new Laplace.Api.Contracts.ErrorBody("not_found", "unknown_route", "No such API route.")),
    statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
