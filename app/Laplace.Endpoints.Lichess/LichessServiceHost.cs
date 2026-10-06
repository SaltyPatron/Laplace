using System.Net;
using Laplace.Chess.Service;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.Lichess;

internal sealed record LichessOptions(
    int Depth = LichessDefaults.SearchDepth,
    int MaxConcurrent = LichessDefaults.MaxConcurrent,
    bool Substrate = true,
    int Port = 5189,
    LichessChallengePolicy? Challenges = null,
    int FailureLingerSeconds = 20)
{
    public static LichessOptions FromEnvironment()
    {
        static int Number(string name, int fallback) => Environment.GetEnvironmentVariable(name) is { } text
            ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        var speeds = Environment.GetEnvironmentVariable("LAPLACE_LICHESS_SPEEDS")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(
            Number("LAPLACE_LICHESS_DEPTH", LichessDefaults.SearchDepth),
            Number("LAPLACE_LICHESS_MAX_CONCURRENT", LichessDefaults.MaxConcurrent),
            Environment.GetEnvironmentVariable("LAPLACE_LICHESS_SUBSTRATE") != "false",
            // the loopback status port the API reads (AppComposition reads the same variable)
            Number("LAPLACE_LICHESS_PORT", 5189),
            Challenges: new LichessChallengePolicy(
                speeds is { Length: > 0 } ? speeds.ToHashSet(StringComparer.OrdinalIgnoreCase) : null,
                // rated challenges only when LAPLACE_LICHESS_RATED=true; casual games otherwise
                Rated: Environment.GetEnvironmentVariable("LAPLACE_LICHESS_RATED") == "true"));
    }
    public void Validate()
    {
        if (Depth is < 1 or > 64 || MaxConcurrent is < 1 or > 16 || Port is < 0 or > 65535 || FailureLingerSeconds is < 0 or > 300)
            throw new InvalidOperationException("Invalid Lichess service limits.");
        if (Challenges?.Speeds?.Any(s => !LichessChallengePolicy.KnownSpeeds.Contains(s)) == true)
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
        // Loopback-only and read-only: health, status and per-game chat. Nothing here
        // starts or stops the worker.
        app.MapGet("/health/live", () => Results.Json(new { service = "laplace-lichess", live = true }));
        app.MapGet("/health/ready", (ILichessConnection bot) =>
        {
            var status = bot.Status();
            // Ready means configured; upstream connectivity is reported in the body
            // but does not gate readiness.
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
            if (!bot.Start(options.Depth, options.MaxConcurrent, options.Substrate, options.Challenges))
                throw new InvalidOperationException("Lichess service could not start; verify server-side token configuration.");
            await bot.WaitForExitAsync(stoppingToken);
            if (!stoppingToken.IsCancellationRequested)
                throw new InvalidOperationException("Lichess worker exited unexpectedly.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var reason = bot.Status().Error;
            log.LogError("Lichess service failed: {ErrorType}: {Reason}", ex.GetType().Name, reason ?? ex.Message);
            failed();
            // Keep answering /status for a while with the failure, so the API and the panel can show why the
            // service is restarting instead of only seeing it vanish (systemd RestartSec / NSSM AppRestartDelay follow).
            try { await Task.Delay(TimeSpan.FromSeconds(options.FailureLingerSeconds), stoppingToken); }
            catch (OperationCanceledException) { }
            lifetime.StopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        await bot.StopAsync(ct);
    }
}
