using System.Text.Json;
using Laplace.Chess.Service;

namespace Laplace.Endpoints.OpenAICompat;

internal static class ChessEndpoints
{
    private static readonly JsonSerializerOptions LabEventJson = new(JsonSerializerDefaults.Web);

    public static void MapChessEndpoints(this WebApplication app)
    {
        app.MapGet("/chess/new", (ChessEngineService svc) =>
            Results.Json(new { fen = svc.NewGameFen() })).WithTags("chess");

        app.MapPost("/chess/legal", async (FenRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.LegalAsync(req.Fen, ct))).WithTags("chess");

        app.MapPost("/chess/move", async (MoveRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.ApplyMoveAsync(req.Fen, req.Uci, ct))).WithTags("chess");

        app.MapPost("/chess/eval", async (EvalRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.EvalPositionAsync(req.Fen, req.Depth ?? 4, req.Substrate ?? true, ct))).WithTags("chess");

        app.MapPost("/chess/bestmove", async (BestMoveRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.BestMoveSearchAsync(req.Fen, req.Depth ?? 4, req.Substrate ?? true, ct))).WithTags("chess");

        app.MapPost("/chess/train/start", (double? temperature, double? weight, int? maxPlies, int? games, ChessEngineService svc) =>
            Results.Json(new { started = svc.StartTraining(temperature ?? 120d, weight ?? 0.5d, maxPlies ?? 400, games ?? 0) }))
            .WithTags("chess");

        app.MapPost("/chess/train/stop", (ChessEngineService svc) =>
            Results.Json(new { stopped = svc.StopTraining() })).WithTags("chess");

        app.MapGet("/chess/train/status", (ChessEngineService svc) =>
            Results.Json(svc.Status())).WithTags("chess");

        app.MapGet("/chess/learned-pst", async (ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.LearnedPstAsync(ct))).WithTags("chess");

        app.MapGet("/chess/lab/catalog", () =>
        {
            var engines = ChessLabPaths.Catalog.ToDictionary(
                kv => kv.Key,
                kv => new { path = kv.Value.Path, found = kv.Value.Found, source = kv.Value.Source });
            return Results.Json(new
            {
                jobs = new object[]
                {
                    new { kind = "substrate-test", label = "Substrate test (guided vs pure)", @default = new { games = "20", depth = "4", mode = "fold" } },
                    new { kind = "ladder", label = "Eval overlay ladder", @default = new { games = "20", depth = "4" } },
                    new { kind = "tactics", label = "Tactics solve rate", @default = new { depth = "6" } },
                    new { kind = "review", label = "PGN review triage", @default = new { depth = "4", maxGames = "10" } },
                    new { kind = "learned-pst", label = "Learned PST grid", @default = new { piece = "PNBRQK" } },
                    new { kind = "cutechess", label = "cutechess vs Stockfish", @default = new { rounds = "10", depth = "8" } },
                    new { kind = "lichess-bot", label = "Lichess bot", @default = new { depth = "4", maxConcurrent = "2" } },
                    new { kind = "lichess-fetch", label = "Fetch player PGN", @default = new { site = "lichess" } },
                },
                engines,
            });
        }).WithTags("chess-lab");

        app.MapPost("/chess/lab/start", (LabStartRequest req, ChessLabService lab) =>
        {
            if (!Enum.TryParse<ChessLabJobKind>(req.Kind?.Replace("-", ""), ignoreCase: true, out var kind)
                && !TryParseKind(req.Kind, out kind))
                return Results.BadRequest(new { error = $"unknown kind '{req.Kind}'" });
            var config = req.Config?.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()) ?? new Dictionary<string, string>();
            var id = lab.StartJob(kind, config);
            return id is null ? Results.Problem("failed to start job") : Results.Json(new { jobId = id });
        }).WithTags("chess-lab");

        app.MapPost("/chess/lab/stop/{jobId}", (string jobId, ChessLabService lab) =>
            Results.Json(new { stopped = lab.StopJob(jobId) })).WithTags("chess-lab");

        app.MapGet("/chess/lab/jobs", (ChessLabService lab) =>
            Results.Json(lab.ListJobs())).WithTags("chess-lab");

        app.MapGet("/chess/lab/jobs/{jobId}", (string jobId, ChessLabService lab) =>
            lab.GetJob(jobId) is { } job ? Results.Json(job) : Results.NotFound()).WithTags("chess-lab");

        app.MapGet("/chess/lab/jobs/{jobId}/events", async (HttpContext ctx, string jobId, ChessLabService lab, CancellationToken ct) =>
        {
            var reader = lab.EventReader(jobId);
            if (reader is null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                // Match the camelCase used by Results.Json elsewhere; default options emit
                // PascalCase, which never matched the web client's field checks.
                var json = JsonSerializer.Serialize(evt, evt.GetType(), LabEventJson);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }).WithTags("chess-lab");

        app.MapGet("/chess/lab/jobs/{jobId}/artifact/{name}", (string jobId, string name, ChessLabService lab) =>
        {
            var job = lab.GetJob(jobId);
            if (job is null || !job.Artifacts.TryGetValue(name, out var path) || !File.Exists(path))
                return Results.NotFound();
            return Results.File(path, "application/x-chess-pgn", name);
        }).WithTags("chess-lab");

        app.MapPost("/chess/lab/jobs/{jobId}/ingest", async (string jobId, ChessLabService lab) =>
        {
            var job = lab.GetJob(jobId);
            if (job is null || !job.Artifacts.TryGetValue("games.pgn", out var path) || !File.Exists(path))
                return Results.NotFound(new { error = "no games.pgn artifact" });
            return Results.Json(new { queued = true, path, hint = $"laplace ingest chess \"{path}\"" });
        }).WithTags("chess-lab");
    }

    private static bool TryParseKind(string? kind, out ChessLabJobKind parsed) => kind?.ToLowerInvariant() switch
    {
        "substrate-test" or "substratetest" => (parsed = ChessLabJobKind.SubstrateTest) == ChessLabJobKind.SubstrateTest,
        "ladder" => (parsed = ChessLabJobKind.Ladder) == ChessLabJobKind.Ladder,
        "tactics" => (parsed = ChessLabJobKind.Tactics) == ChessLabJobKind.Tactics,
        "review" => (parsed = ChessLabJobKind.Review) == ChessLabJobKind.Review,
        "learned-pst" or "learnedpst" => (parsed = ChessLabJobKind.LearnedPst) == ChessLabJobKind.LearnedPst,
        "cutechess" => (parsed = ChessLabJobKind.Cutechess) == ChessLabJobKind.Cutechess,
        "lichess-bot" or "lichessbot" => (parsed = ChessLabJobKind.LichessBot) == ChessLabJobKind.LichessBot,
        "lichess-fetch" or "lichessfetch" => (parsed = ChessLabJobKind.LichessFetch) == ChessLabJobKind.LichessFetch,
        _ => (parsed = default) == default && false,
    };

    private sealed record FenRequest(string Fen);
    private sealed record MoveRequest(string Fen, string Uci);
    private sealed record EvalRequest(string Fen, int? Depth, bool? Substrate);
    private sealed record BestMoveRequest(string Fen, double? Temperature, int? Depth, bool? Substrate);
    private sealed record LabStartRequest(string? Kind, Dictionary<string, JsonElement>? Config);
}
