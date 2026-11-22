using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
using Laplace.Ops;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

namespace Laplace.Hosting;

public sealed record LaplaceHostOptions(
    string ServiceName,
    int DefaultPort,
    int RateLimitPerClientPerMinute = 300,
    int WebhookPerMinute = 120,
    int PublicQueryConcurrencyLimit = 16,
    int ClientDiagnosticsPerMinute = 60,
    bool MapHealth = true);

public static class LaplaceHost
{
    public static bool IsOpenApiDocumentGeneration => string.Equals(
        Assembly.GetEntryAssembly()?.GetName().Name,
        "GetDocument.Insider",
        StringComparison.Ordinal);

    public static async Task<int> RunAsync(
        string serviceName,
        string[] args,
        Func<PersistentLogHealth, Task<int>> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(run);

        if (OperatingSystem.IsWindows() && ManagedServiceState.OperatorStopped(serviceName))
        {
            Console.WriteLine($"laplace-{serviceName}: stopped by an operator ({ManagedServiceState.StopMarker(serviceName)}); not starting.");
            return 0;
        }

        var logHealth = new PersistentLogHealth();
        if (IsOpenApiDocumentGeneration)
            return await run(logHealth);

        var component = $"laplace-{serviceName}";
        int exitCode;
        try
        {
            exitCode = await run(logHealth);
        }
        catch (Exception ex)
        {
            LaplaceLogging.ReportProcessFailure(
                component,
                ex,
                failure => Log.ForContext("SourceContext", "Laplace.Hosting").Fatal(failure, "Fatal error in {Component}", component),
                logHealth);
            exitCode = 1;
        }

        return LaplaceLogging.CompleteProcess(Log.CloseAndFlush, component, exitCode, logHealth);
    }
}

public static class LaplaceHostingExtensions
{
    public static WebApplicationBuilder AddLaplaceHosting(
        this WebApplicationBuilder builder,
        LaplaceHostOptions options,
        PersistentLogHealth logHealth)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logHealth);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(logHealth);

        if (!LaplaceHost.IsOpenApiDocumentGeneration)
        {
            builder.Host.UseSerilog((_, _, cfg) =>
                cfg.MinimumLevel.Information()
                   .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                   .ApplyLaplaceSinks(
                       options.ServiceName,
                       console: true,
                       consoleToStdErr: false,
                       health: logHealth));
        }

        ConfigureUrls(builder, options);

        builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
        });

        builder.Services.AddResponseCompression(compression =>
        {
            compression.EnableForHttps = true;
            compression.MimeTypes = ResponseCompressionDefaults.MimeTypes;
        });

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            if (options.RateLimitPerClientPerMinute > 0)
            {
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, IPAddress>(context =>
                {
                    var ip = context.Connection.RemoteIpAddress ?? IPAddress.Loopback;
                    if (IPAddress.IsLoopback(ip))
                        return RateLimitPartition.GetNoLimiter(ip);

                    return RateLimitPartition.GetFixedWindowLimiter(
                        ip,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = options.RateLimitPerClientPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0,
                        });
                });
            }

            limiter.AddConcurrencyLimiter("public-query", o =>
            {
                o.PermitLimit = Math.Max(1, options.PublicQueryConcurrencyLimit);
                o.QueueLimit = 0;
                o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });

            limiter.AddFixedWindowLimiter("webhook", o =>
            {
                o.PermitLimit = Math.Max(1, options.WebhookPerMinute);
                o.Window = TimeSpan.FromMinutes(1);
                o.QueueLimit = 0;
            });

            limiter.AddFixedWindowLimiter("client-diagnostics", o =>
            {
                o.PermitLimit = Math.Max(1, options.ClientDiagnosticsPerMinute);
                o.Window = TimeSpan.FromMinutes(1);
                o.QueueLimit = 0;
            });
        });

        ConfigureOpenTelemetry(builder, options);
        return builder;
    }

    public static WebApplication InitializeLaplaceLogging(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetService<LaplaceHostOptions>();
        if (options is not null && !LaplaceHost.IsOpenApiDocumentGeneration)
        {
            app.Logger.LogInformation(
                "Initialized hosting for service {ServiceName} on {MachineName}",
                options.ServiceName,
                Environment.MachineName);
        }
        return app;
    }

    public static WebApplication UseLaplaceHosting(
        this WebApplication app,
        Uri? publicOrigin,
        Action<IApplicationBuilder>? beforeLogging = null,
        Action<IApplicationBuilder>? beforeCompression = null,
        Action<IApplicationBuilder>? afterAuth = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseForwardedHeaders();
        if (publicOrigin is not null)
        {
            app.Use((context, next) =>
            {
                if (string.IsNullOrEmpty(context.Request.Scheme))
                    context.Request.Scheme = publicOrigin.Scheme;
                return next();
            });
        }

        beforeLogging?.Invoke(app);

        if (!LaplaceHost.IsOpenApiDocumentGeneration && app.Services.GetService<Serilog.IDiagnosticContext>() is not null)
        {
            app.UseSerilogRequestLogging(o =>
            {
                o.GetLevel = (ctx, _, ex) =>
                    ex is not null || ctx.Response.StatusCode >= 500
                        ? LogEventLevel.Error
                        : ctx.Request.Path.StartsWithSegments("/health")
                            ? LogEventLevel.Verbose
                            : LogEventLevel.Information;
            });
        }

        beforeCompression?.Invoke(app);

        app.UseResponseCompression();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();

        if (app.Services.GetService<IAuthenticationSchemeProvider>() is not null)
            app.UseAuthentication();
        if (app.Services.GetService<IAuthorizationPolicyProvider>() is not null)
            app.UseAuthorization();

        afterAuth?.Invoke(app);

        var options = app.Services.GetService<LaplaceHostOptions>();
        if (options?.MapHealth == true)
        {
            app.MapGet("/health", () => Results.Json(new
            {
                service = $"laplace-{options.ServiceName}",
                live = true,
                host = Environment.MachineName,
            }));
            app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = options.ServiceName }));
        }

        return app;
    }

    private static void ConfigureUrls(WebApplicationBuilder builder, LaplaceHostOptions options)
    {
        if (LaplaceHost.IsOpenApiDocumentGeneration)
            return;

        if (!string.IsNullOrEmpty(builder.Configuration["urls"])
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_PORT")))
        {
            return;
        }

        var portValue = Environment.GetEnvironmentVariable("LAPLACE_PORT");
        int port = int.TryParse(portValue, out var parsed) && parsed is > 0 and < 65536
            ? parsed
            : options.DefaultPort;

        if (port is > 0 and < 65536)
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    }

    private static void ConfigureOpenTelemetry(WebApplicationBuilder builder, LaplaceHostOptions options)
    {
        if (LaplaceHost.IsOpenApiDocumentGeneration)
            return;

        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        bool hasOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService($"laplace-{options.ServiceName}"))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation();
                if (hasOtlp) t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                 .AddPrometheusExporter();
                if (hasOtlp) m.AddOtlpExporter();
            });
    }
}
