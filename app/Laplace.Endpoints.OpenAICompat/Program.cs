using System.Net;
using System.Threading.RateLimiting;
using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Microsoft.AspNetCore.HttpOverrides;
using Laplace.Endpoints.OpenAICompat;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = LaplaceInstall.InstallRoot,
    WebRootPath = LaplaceInstall.WebRoot,
});

builder.WebHost.ConfigureKestrel(options =>
    options.ListenLocalhost(LaplaceInstall.EndpointPort));

builder.Host.UseSerilog((_, lc) =>
{
    lc.MinimumLevel.Information().Enrich.FromLogContext();
    lc.WriteTo.Console();
});

builder.Services.AddOpenAiCompatServices();
builder.Services.AddOpenApi();

const int perTenantPerMinute = 300;
const int webhookPerMinute = 120;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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

        var tenant = ctx.Request.Headers["X-Laplace-Tenant"].ToString();
        if (string.IsNullOrWhiteSpace(tenant)) tenant = "local-dev";
        return RateLimitPartition.GetSlidingWindowLimiter($"tenant:{tenant}",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = perTenantPerMinute,
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
app.MapOpenAiCompatEndpoints();
app.MapFoundryEndpoints();
app.MapBillingEndpoints();
app.MapChessEndpoints();
app.MapFeedbackEndpoints();

app.MapFallback("/v1/{*path}", () => Results.Json(
    new Laplace.Api.Contracts.ErrorResponse(
        new Laplace.Api.Contracts.ErrorBody("not_found", "unknown_route", "No such API route.")),
    statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
