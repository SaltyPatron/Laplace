using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Laplace.Chess.Service.Uci;
using Laplace.Chess.Uci.Commands;
using Laplace.Chess.Uci.Engines;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.Engines;

/// <summary>Where the endpoint listens and what it asks for. The token comes from LAPLACE_ENGINES_TOKEN (the
/// environment, or deploy/secrets/engines.env); without one the service does not start.</summary>
internal sealed record EnginesOptions(string Bind = "0.0.0.0", int Port = 5190, string? Token = null, string Profile = "analysis")
{
    public static EnginesOptions FromEnvironment() => new(
        LaplaceInstall.TryReadConfig("LAPLACE_ENGINES_BIND", "engines.env") ?? "0.0.0.0",
        int.TryParse(LaplaceInstall.TryReadConfig("LAPLACE_ENGINES_PORT", "engines.env"), out var port) ? port : 5190,
        LaplaceInstall.TryReadConfig("LAPLACE_ENGINES_TOKEN", "engines.env"),
        LaplaceInstall.TryReadConfig("LAPLACE_ENGINES_PROFILE", "engines.env") ?? "analysis");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Token) || Token.Length < 24)
            throw new InvalidOperationException("LAPLACE_ENGINES_TOKEN is not configured (deploy/secrets/engines.env); the engine endpoint does not run open.");
        if (!IPAddress.TryParse(Bind, out _) || Port is < 0 or > 65535)
            throw new InvalidOperationException("Invalid engine endpoint bind address or port.");
    }
}

public sealed record LimitsRequest(long? Nodes = null, int? Depth = null, long? MovetimeMs = null);
public sealed record ClockRequest(long WtimeMs, long BtimeMs, long WincMs = 0, long BincMs = 0, long OverheadMs = 0);

/// <summary>One analyse or bestmove request. Position: fen (default the start position) and moves in UCI notation.
/// Options override the profile for this job only (canonical or engine names); state is fresh (default) or warm;
/// waitMs bounds how long to wait for a free engine before answering busy.</summary>
public sealed record EngineRequest(string Engine, string? Fen = null, IReadOnlyList<string>? Moves = null,
    LimitsRequest? Limits = null, ClockRequest? Clock = null, int? MultiPv = null,
    Dictionary<string, string>? Options = null, string? Profile = null, string? State = null,
    int? TimeoutMs = null, int? WaitMs = null);

public sealed record BestMoveResponse(string Engine, string? BestMove, string? Ponder, EngineScore? Score, EngineWdl? Wdl,
    EngineReceipt Receipt);

/// <summary>
/// The engines behind the endpoint: one <see cref="EnginePool{T}"/> per engine, its capacity the catalog's process
/// limit (Lc0: one, the GPU). A slot holds at most one process, reopened when a job needs another profile or options,
/// so an engine never has more processes than its capacity. A job never falls back to another engine.
/// </summary>
internal sealed class EngineHub : IDisposable
{
    private readonly Dictionary<string, EnginePool<Slot>> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _profile;
    private readonly Func<string, string, ChessEngineSpec> _resolve;

    public EngineHub(string profile, Func<string, string, ChessEngineSpec>? resolve = null)
    {
        _profile = profile;
        _resolve = resolve ?? ((engine, p) => ChessEngineCatalog.Resolve(engine, p));
    }

    private sealed class Slot : IDisposable
    {
        public EngineSession? Session;
        public string Signature = "";
        public void Dispose() => Session?.Dispose();
    }

    public EngineAnalysis Run(EngineRequest request, CancellationToken ct)
    {
        var wall = Stopwatch.StartNew();
        string engine = request.Engine?.Trim().ToLowerInvariant() ?? "";
        if (!ChessEngineCatalog.Names.Contains(engine))
            throw new UciEngineException(UciFailure.Rejected, $"unknown engine '{request.Engine}' (known: {string.Join(", ", ChessEngineCatalog.Names)})");
        string profile = request.Profile ?? (request.Clock is null ? _profile : "play");
        var spec = _resolve(engine, profile);
        if (!spec.Available) throw new UciEngineException(UciFailure.Unavailable, spec.Missing ?? $"{engine} is not installed");

        ChessPosition position;
        try { position = ChessPosition.From(request.Fen, request.Moves); }
        catch (FormatException ex) { throw new UciEngineException(UciFailure.Rejected, ex.Message, ex); }
        var limits = Limits(request, position);
        int multiPv = request.MultiPv ?? 1;
        if (multiPv is < 1 or > 32) throw new UciEngineException(UciFailure.Rejected, "multipv is 1..32");
        var options = request.Options ?? [];
        string signature = profile + "|" + string.Join(";", options.OrderBy(static o => o.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static o => o.Key + "=" + o.Value));

        var pool = Pool(engine, spec.MaxProcesses);
        var slot = pool.TryRent(TimeSpan.FromMilliseconds(Math.Clamp(request.WaitMs ?? 2000, 0, 60_000)), ct)
            ?? throw new UciEngineException(UciFailure.Busy, $"{engine}: all {pool.Capacity} engine process(es) are busy");
        try
        {
            if (slot.Session is null || slot.Session.Broken || slot.Signature != signature)
            {
                slot.Session?.Dispose();
                slot.Session = null;
                slot.Session = EngineSession.Open(spec, options);
                slot.Signature = signature;
            }
            long queueMs = wall.ElapsedMilliseconds;
            var analysis = slot.Session.Analyse(position, limits, multiPv, request.State ?? (request.Clock is null ? "fresh" : "warm"),
                request.TimeoutMs is > 0 and var t ? TimeSpan.FromMilliseconds(Math.Min(t, 600_000)) : null, ct: ct);
            return analysis with
            {
                Receipt = analysis.Receipt with { QueueMs = queueMs, WallMs = wall.ElapsedMilliseconds, OverheadMs = request.Clock?.OverheadMs },
            };
        }
        finally
        {
            pool.Return(slot);
        }
    }

    private static UciLimits Limits(EngineRequest request, ChessPosition position)
    {
        if (request.Clock is { } c)
        {
            if (c.WtimeMs < 0 || c.BtimeMs < 0 || c.OverheadMs < 0) throw new UciEngineException(UciFailure.Rejected, "clock values are non-negative");
            return new UciClock(c.WtimeMs, c.BtimeMs, c.WincMs, c.BincMs, c.OverheadMs).ToLimits(request.Limits?.Depth);
        }
        var l = request.Limits ?? new LimitsRequest();
        var limits = new UciLimits(Nodes: l.Nodes, Depth: l.Depth, MoveTimeMs: l.MovetimeMs);
        if (!limits.Bounded) throw new UciEngineException(UciFailure.Rejected, "limits need nodes, depth or movetimeMs (or a clock)");
        if (l.Nodes is > 1_000_000_000 || l.Depth is > 64 || l.MovetimeMs is > 600_000)
            throw new UciEngineException(UciFailure.Rejected, "limits exceed nodes 1e9, depth 64 or movetimeMs 600000");
        return limits;
    }

    private EnginePool<Slot> Pool(string engine, int capacity)
    {
        lock (_gate)
        {
            if (!_pools.TryGetValue(engine, out var pool))
                _pools[engine] = pool = new EnginePool<Slot>(() => new Slot(), static s => s.Session?.Broken == true, Math.Max(1, capacity));
            return pool;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var pool in _pools.Values) pool.Dispose();
            _pools.Clear();
        }
    }
}

internal static class EnginesHost
{
    public static WebApplication Build(EnginesOptions options, EngineHub? hub = null,
        Func<IReadOnlyList<EngineListing>>? listEngines = null)
    {
        options.Validate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Parse(options.Bind), options.Port);
            k.Limits.MaxRequestBodySize = 64 * 1024;
        });
        builder.Services.AddSingleton(hub ?? new EngineHub(options.Profile));
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.DefaultIgnoreCondition = UciJson.Options.DefaultIgnoreCondition;
            foreach (var converter in UciJson.Options.Converters) o.SerializerOptions.Converters.Add(converter);
        });
        var app = builder.Build();
        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + options.Token));
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/health")) { await next(context); return; }
            byte[] given = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(given, expected))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = new CommandError("unauthorized", "a bearer token is required") });
                return;
            }
            await next(context);
        });

        app.MapGet("/health", () => Results.Json(new { service = "laplace-engines", live = true, host = Environment.MachineName }));
        app.MapGet("/engines", () => Results.Json(new
        {
            host = Environment.MachineName,
            profile = options.Profile,
            engines = (listEngines ?? (() => ChessCommands.Engines(options.Profile)))(),
        }, UciJson.Options));
        app.MapPost("/analyse", (EngineRequest request, EngineHub engines, CancellationToken ct)
            => Answer(() => engines.Run(request, ct)));
        app.MapPost("/bestmove", (EngineRequest request, EngineHub engines, CancellationToken ct)
            => Answer(() =>
            {
                var analysis = engines.Run(request with { Profile = request.Profile ?? "play" }, ct);
                return new BestMoveResponse(analysis.Receipt.Engine.Via?.Name ?? request.Engine, analysis.BestMove.Move?.Text,
                    analysis.BestMove.Ponder?.Text, analysis.Final?.Score, analysis.Final?.Wdl, analysis.Receipt);
            }));
        return app;
    }

    /// <summary>Typed errors: rejected 400, busy 429, unavailable 503, timeout 504, a protocol fault 502.</summary>
    private static async Task<IResult> Answer<T>(Func<T> run)
    {
        try { return Results.Json(await Task.Run(run), UciJson.Options); }
        catch (UciEngineException ex)
        {
            int status = ex.Failure switch
            {
                UciFailure.Rejected => 400,
                UciFailure.Busy => 429,
                UciFailure.Unavailable => 503,
                UciFailure.Timeout => 504,
                _ => 502,
            };
            return Results.Json(new { error = CommandError.From(ex) }, UciJson.Options, statusCode: status);
        }
    }
}
