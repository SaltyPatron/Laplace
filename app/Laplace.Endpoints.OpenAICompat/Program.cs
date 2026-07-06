using System.Net;
using System.Threading.RateLimiting;
using Laplace.Chess.Service;
using Microsoft.AspNetCore.HttpOverrides;
using Laplace.Endpoints.OpenAICompat;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

ChessLabPaths.LoadEnvFile();

var builder = WebApplication.CreateBuilder(args);



builder.Host.UseSerilog((_, lc) =>
{
    lc.MinimumLevel.Information().Enrich.FromLogContext();
    if (string.Equals(Environment.GetEnvironmentVariable("LAPLACE_LOG_JSON"), "true", StringComparison.OrdinalIgnoreCase))
        lc.WriteTo.Console(new CompactJsonFormatter());
    else
        lc.WriteTo.Console();
    var logDir = Environment.GetEnvironmentVariable("LAPLACE_LOG_DIR");
    if (!string.IsNullOrWhiteSpace(logDir))
        lc.WriteTo.File(new CompactJsonFormatter(), Path.Combine(logDir, "endpoint-.json"),
            rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14);
});

builder.Services.AddOpenAiCompatServices();
builder.Services.AddOpenApi();



var corsOrigins = (Environment.GetEnvironmentVariable("LAPLACE_CORS_ORIGINS") ?? string.Empty)
    .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyMethod()
        .WithHeaders("Content-Type", "Authorization", "X-Laplace-Tenant", "X-Laplace-Quote-Id", "X-Correlation-Id")
        .WithExposedHeaders("X-Correlation-Id")));
}



var perTenantPerMinute = ReadIntEnv("LAPLACE_RATELIMIT_PERMIN", 300);
var webhookPerMinute = ReadIntEnv("LAPLACE_RATELIMIT_WEBHOOK_PERMIN", 120);
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
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddNpgsql();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
            tracing.AddOtlpExporter();
    });

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

if (corsOrigins.Length > 0)
    app.UseCors();
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
app.MapOpenAiCompatEndpoints();
app.MapBillingEndpoints();
app.MapChessEndpoints();
app.MapFeedbackEndpoints();


app.MapFallback("/v1/{*path}", () => Results.Json(
    new Laplace.Api.Contracts.ErrorResponse(
        new Laplace.Api.Contracts.ErrorBody("not_found", "unknown_route", "No such API route.")),
    statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();

static int ReadIntEnv(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

public partial class Program;
