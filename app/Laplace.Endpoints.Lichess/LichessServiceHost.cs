using System.Net;
using Laplace.Chess.Service;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.Lichess;

internal sealed record LichessOptions(int Depth = DefaultDepth, int MaxConcurrent = 2, bool Substrate = true,
    int Port = 5189, IReadOnlySet<string>? Speeds = null)
{
    public const int DefaultDepth = 6;

    public static LichessOptions FromEnvironment()
    {
        static int Number(string name, int fallback) => Environment.GetEnvironmentVariable(name) is { } text
            ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        var speeds = Environment.GetEnvironmentVariable("LAPLACE_LICHESS_SPEEDS")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(Number("LAPLACE_LICHESS_DEPTH", DefaultDepth), Number("LAPLACE_LICHESS_MAX_CONCURRENT", 2),
            Environment.GetEnvironmentVariable("LAPLACE_LICHESS_SUBSTRATE") != "false",
            Speeds: speeds is { Length: > 0 } ? speeds.ToHashSet(StringComparer.OrdinalIgnoreCase) : null);
    }
    public void Validate()
    {
        if (Depth is < 1 or > 64 || MaxConcurrent is < 1 or > 16 || Port is < 0 or > 65535)
            throw new InvalidOperationException("Invalid Lichess service limits.");
        if (Speeds?.Any(s => s is not ("ultraBullet" or "bullet" or "blitz" or "rapid" or "classical" or "correspondence")) == true)
            throw new InvalidOperationException("Unsupported Lichess speed filter.");
    }
}

internal static class LichessServiceHost
{
    public static WebApplication Build(LichessOptions options, ILichessConnection? connection = null, Action? failed = null)
    {
        options.Validate();
        var database = connection is null ? ManagedServiceDatabase.Resolve() : null;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(failed ?? (() => Environment.ExitCode = 1));
        builder.Services.AddSingleton<ILichessConnection>(sp => connection ?? new LichessConnectivityService(
            ct => ChessLiveGameHost.CreateAsync(0.5, ct: ct, connString: database),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("lichess"), ownsHost: true));
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(40));
        builder.Services.AddHostedService<LichessWorker>();
        var app = builder.Build();
        // The listener has no start/stop route and is loopback-only. All service
        // mutations go through the separately authenticated API/control helper.
        app.MapGet("/health/live", () => Results.Json(new { service = "laplace-lichess", live = true }));
        app.MapGet("/health/ready", (ILichessConnection bot) =>
        {
            var status = bot.Status();
            // Deployment readiness proves that the configured worker process is alive
            // and can serve its status/control surface. Lichess connectivity is an
            // external product integration and may flap independently; /status and
            // post-delivery QA report it without rolling a healthy API/SPA payload back.
            bool ready = status.Configured;
            return Results.Json(new
            {
                service = "laplace-lichess",
                ready,
                connected = status.Connected,
                error = status.Error
            }, statusCode: ready ? 200 : 503);
        });
        app.MapGet("/status", (ILichessConnection bot) => Results.Json(bot.Status()));
        app.MapGet("/games/{gameId}/chat", (string gameId, ILichessConnection bot) =>
            gameId.Length <= 32 && gameId.All(char.IsAsciiLetterOrDigit)
                ? Results.Json(bot.ChatForGame(gameId)) : Results.BadRequest());
        return app;
    }
}

internal sealed class LichessWorker(ILichessConnection bot, LichessOptions options,
    IHostApplicationLifetime lifetime, ILogger<LichessWorker> log, Action failed) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!bot.Start(options.Depth, options.MaxConcurrent, options.Substrate, options.Speeds))
                throw new InvalidOperationException("Lichess service could not start; verify server-side token configuration.");
            await bot.WaitForExitAsync(stoppingToken);
            if (!stoppingToken.IsCancellationRequested)
                throw new InvalidOperationException("Lichess worker exited unexpectedly.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogError("Lichess service failed: {ErrorType}", ex.GetType().Name);
            failed();
            lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        await bot.StopAsync(ct);
    }
}
