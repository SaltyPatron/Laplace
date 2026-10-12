using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Laplace.Chess.Service;
using Laplace.Chess.Service.Uci;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.Lichess;

internal sealed record LichessSelection(string? Profile, long Revision, string? Actor, DateTimeOffset ChangedUtc,
    string? ReadSource = null, string? RecordingTarget = null);
internal sealed record LichessSelectionRequest(string? Profile, long ExpectedRevision, string Actor,
    string? ReadSource = null, string? RecordingTarget = null);

internal static class ControlledLichessHost
{
    public static WebApplication Build(string path)
    {
        var config = LichessControlConfiguration.Read(path);
        var token = File.ReadAllText(config.TokenFile).Trim();
        if (token.Length < 32 || token.Any(char.IsWhiteSpace)) throw new InvalidDataException("Invalid Lichess control credential.");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, config.Port));
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<LichessController>();
        builder.Services.AddHostedService(s => s.GetRequiredService<LichessController>());
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(40));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(config.TokenFile).Trim()));
            var header = context.Request.Headers.Authorization.ToString();
            var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : ""));
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        app.MapGet("/status", (LichessController controller) => Results.Json(controller.Status()));
        app.MapPost("/selection", async (LichessSelectionRequest request, LichessController controller, CancellationToken ct) =>
        {
            try { return Results.Json(await controller.SelectAsync(request, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        return app;
    }
}

internal sealed class LichessController : BackgroundService
{
    private readonly LichessControlConfiguration config;
    private readonly ILogger<LichessController> log;
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly FileStream ownership;
    private LichessSelection selection;
    private LichessConnectivityService? bot;
    private string? activeProfile;
    private LichessSelection? activeSelection;
    private string? failure;
    private string phase = "off";
    private long attemptedRevision = -1;

    public LichessController(LichessControlConfiguration config, ILogger<LichessController> log)
    {
        this.config = config; this.log = log;
        ownership = new FileStream(config.StateFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        selection = File.Exists(config.StateFile)
            ? JsonSerializer.Deserialize<LichessSelection>(File.ReadAllText(config.StateFile), LichessControlConfiguration.Json)
                ?? throw new InvalidDataException("Empty Lichess selection state.")
            : new(null, 0, null, DateTimeOffset.UtcNow);
        if (config.Routing is not null) selection = config.Routing.Restore(selection);
        if (selection.Profile is not null)
            _ = config.Resolve(selection.Profile, selection.ReadSource, selection.RecordingTarget);
    }

    public object Status() => new
    {
        controller = Environment.MachineName, selection = Volatile.Read(ref selection),
        activeProfile, activeSelection, phase, error = failure, bot = bot?.Status(),
        routing = config.Routing is not { } routing ? null : new
        {
            executors = routing.Executors.Select(p => new { id = p.Key, p.Value.Label, p.Value.Environment,
                p.Value.Implementation, p.Value.MaxConcurrent, p.Value.Rated,
                sources = p.Value.Sources.Select(s => new { id = s.Key, recordingTargets = s.Value.RecordingTargets }) }),
            sources = routing.Sources.Select(p => new { id = p.Key, p.Value.Label, p.Value.Implementation, p.Value.Identity }),
            recordingTargets = routing.RecordingTargets.Select(p => new { id = p.Key, p.Value.Label, p.Value.Environment,
                p.Value.Implementation, p.Value.Identity })
        },
        profiles = config.Profiles.Select(p => new { id = p.Key, p.Value.Label, p.Value.Environment,
            p.Value.Implementation, p.Value.Database, p.Value.MaxConcurrent, p.Value.Rated, p.Value.Speeds })
    };

    public async Task<object> SelectAsync(LichessSelectionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 160)
            throw new ArgumentException("An authenticated actor is required.");
        if (request.Profile is not null)
            _ = config.Resolve(request.Profile, request.ReadSource, request.RecordingTarget);
        else if (request.ReadSource is not null || request.RecordingTarget is not null)
            throw new ArgumentException("An off selection cannot name a read source or recording destination.");
        await changes.WaitAsync(ct);
        try
        {
            if (selection.Revision != request.ExpectedRevision)
                throw new InvalidOperationException("The selection changed in another session; refresh before applying.");
            var next = new LichessSelection(request.Profile, checked(selection.Revision + 1), request.Actor, DateTimeOffset.UtcNow,
                request.ReadSource, request.RecordingTarget);
            var temporary = config.StateFile + ".new";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next, LichessControlConfiguration.Json), ct);
            File.Move(temporary, config.StateFile, overwrite: true);
            Volatile.Write(ref selection, next);
            log.LogInformation("Lichess selection revision {Revision}: {Profile}, actor {Actor}", next.Revision, next.Profile ?? "off", next.Actor);
            return Status();
        }
        finally { changes.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var desired = Volatile.Read(ref selection);
                if (attemptedRevision != desired.Revision)
                {
                    if (bot is not null)
                    {
                        phase = "draining";
                        // Stop accepting challenges; the existing game's engine and writer
                        // stay paired until every confirmed game finishes.
                        await bot.StopAsync(stoppingToken);
                        await bot.DisposeAsync(); bot = null; activeProfile = null; activeSelection = null;
                    }
                    desired = Volatile.Read(ref selection);
                    attemptedRevision = desired.Revision;
                    failure = null;
                    if (desired.Profile is null) phase = "off";
                    else
                    {
                        phase = "verifying";
                        try
                        {
                            var profile = config.Resolve(desired.Profile, desired.ReadSource, desired.RecordingTarget);
                            var connection = LichessControlConfiguration.Connection(profile);
                            using var source = LaplaceDataSource.Create(SubstrateAccess.Serving, connection);
                            var writer = ChessDatabaseIdentity.Read(source);
                            if (profile.WriteIdentity is not null && writer != profile.WriteIdentity)
                                throw new InvalidOperationException("Recording destination does not match its installed cluster and database identity.");
                            var engine = config.Engine(profile, profile.ReadIdentity ?? writer);
                            using (engine.Open()) { }
                            var live = await ChessLiveGameHost.CreateAsync(ct: stoppingToken, connString: connection);
                            bot = new LichessConnectivityService(live, log, ownsHost: true, drainGames: true);
                            if (!bot.Start(profile.MaxConcurrent, profile.Policy, engine))
                                throw new InvalidOperationException(bot.Status().Error ?? "Lichess worker could not start.");
                            activeProfile = desired.Profile; activeSelection = desired; phase = "running";
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failure = ex is UciEngineException ? ex.Message : $"{ex.GetType().Name}: profile initialization failed; inspect the controller journal.";
                            phase = "failed";
                            log.LogError(ex, "Lichess profile {Profile} failed to initialize", desired.Profile);
                        }
                    }
                }
                if (phase == "running" && bot?.Status() is { Running: false } status)
                { phase = "failed"; failure = status.Error ?? "Lichess worker exited."; }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            if (bot is not null)
            {
                bot.Stop();
                try { await bot.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (TimeoutException) { log.LogWarning("Shutdown interrupted a game drain; no game outcome was fabricated."); }
            }
        }
    }

    public override void Dispose() { ownership.Dispose(); changes.Dispose(); base.Dispose(); }
}
