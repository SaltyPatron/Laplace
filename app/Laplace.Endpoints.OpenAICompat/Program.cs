using System.Net;
using System.Threading.RateLimiting;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Microsoft.AspNetCore.HttpOverrides;
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

        // Before authentication, headers and presented keys are untrusted. Rotating
        // either must not manufacture fresh request budgets.
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

app.UseRateLimiter();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionEnvelopeMiddleware>();
app.UseMiddleware<Laplace.Endpoints.OpenAICompat.Auth.ApiKeyEnforcementMiddleware>();

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

app.MapPrometheusScrapingEndpoint();
app.MapOpenApi();
app.MapCoreEndpoints();
app.MapQueryEndpoints();
app.MapOpEndpoints();
app.MapAdminEndpoints();
app.MapServiceControlEndpoints();
app.MapOpenAiCompatEndpoints();
app.MapFoundryEndpoints();
app.MapBillingEndpoints();
app.MapBillingIdentityEndpoints();
app.MapChessEndpoints();
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
