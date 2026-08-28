using System.Text.Json;
using Laplace.Chess.Service;
using Microsoft.Extensions.Options;

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
            Results.Json(await svc.ApplyMoveAsync(req.Fen, req.Uci, req.Moves, ct))).WithTags("chess");

        app.MapPost("/chess/eval", async (EvalRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.EvalPositionAsync(req.Fen, req.Depth ?? 4, req.Substrate ?? true, ct))).WithTags("chess");

        app.MapPost("/chess/bestmove", async (BestMoveRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.BestMoveSearchAsync(req.Fen, req.Depth ?? 4, req.Substrate ?? true, req.Moves, ct))).WithTags("chess");

        // Opening explorer / player repertoire over the rated MOVE consensus.
        // player is a display name ("Magnus Carlsen", "magnuscarlsen") — resolved
        // through the same PlayerAlias canonicalization the ingest lanes use.
        app.MapPost("/chess/explore", async (ExploreRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.ExploreAsync(req.Fen, req.Player, req.Limit ?? 12, ct))).WithTags("chess");

        app.MapPost("/chess/train/start", (double? temperature, double? weight, int? maxPlies, int? games, ChessEngineService svc) =>
            Results.Json(new { started = svc.StartTraining(temperature ?? 120d, weight ?? 0.5d, maxPlies ?? 400, games ?? 0) }))
            .WithTags("chess");

        app.MapPost("/chess/train/stop", (ChessEngineService svc) =>
            Results.Json(new { stopped = svc.StopTraining() })).WithTags("chess");

        app.MapGet("/chess/train/status", (ChessEngineService svc) =>
            Results.Json(svc.Status())).WithTags("chess");

        app.MapGet("/chess/learned-pst", async (ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.LearnedPstAsync(ct))).WithTags("chess");

        app.MapPost("/chess/play/start", async (PlayStartRequest req, ChessEngineService svc, CancellationToken ct) =>
            // tenant/user are spec-34 provenance for the play session (stubbed until auth):
            // tenant defaults to the shared "public" world, user is optional attribution.
            Results.Json(await svc.StartPlaySessionAsync(req.Record ?? true, req.Moves,
                req.Tenant ?? "public", req.User, ct))).WithTags("chess");

        app.MapPost("/chess/play/move", async (PlayMoveRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.PlayMoveAsync(req.SessionId, req.Fen, req.Uci, ct))).WithTags("chess");

        app.MapPost("/chess/play/bestmove", async (PlayBestMoveRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.PlayBestMoveAsync(req.SessionId, req.Fen, req.Depth ?? 4, req.Substrate ?? true, ct)))
            .WithTags("chess");

        app.MapPost("/chess/play/finish", async (PlayFinishRequest req, ChessEngineService svc, CancellationToken ct) =>
        {
            await svc.FinishPlaySessionAsync(req.SessionId, req.Status ?? "draw", req.Adjudicated ?? false, ct);
            return Results.Json(new { finished = true });
        }).WithTags("chess");

        app.MapGet("/chess/lichess/games/{gameId}/chat", async (string gameId, ILichessStatusClient lichess, CancellationToken ct) =>
            Results.Json(await lichess.ChatAsync(gameId, ct))).WithTags("chess");

        app.MapGet("/chess/lichess/status", async (ILichessStatusClient lichess, CancellationToken ct) =>
            Results.Json(await lichess.StatusAsync(ct))).WithTags("chess");

        app.MapPost("/chess/lichess/start", async (HttpRequest request, LichessStartRequest req,
            IServiceControl services, IOptions<Auth.LaplaceAuthOptions> auth, CancellationToken ct) =>
        {
            if (!Auth.OperatorAuth.IsAuthorized(request, auth.Value)) return Results.Unauthorized();
            if (!ServiceControlEndpoints.IsSafeTransport(request)) return Results.BadRequest(new { error = "https_required" });
            if (req.Depth is not null || req.MaxConcurrent is not null || req.Substrate is not null || req.Speeds is not null)
                return Results.BadRequest(new { error = "managed_configuration", message = "Set LAPLACE_LICHESS_* server-side and restart the service." });
            return await ServiceControlEndpoints.ExecuteAsync(services, ManagedService.Lichess, ServiceAction.Start, ct);
        }).WithTags("chess");

        app.MapPost("/chess/lichess/stop", async (HttpRequest request, IServiceControl services,
            IOptions<Auth.LaplaceAuthOptions> auth, CancellationToken ct) =>
        {
            if (!Auth.OperatorAuth.IsAuthorized(request, auth.Value)) return Results.Unauthorized();
            if (!ServiceControlEndpoints.IsSafeTransport(request)) return Results.BadRequest(new { error = "https_required" });
            return await ServiceControlEndpoints.ExecuteAsync(services, ManagedService.Lichess, ServiceAction.Stop, ct);
        }).WithTags("chess");

        app.MapGet("/chess/lab/catalog", () =>
        {
            var engines = ChessLabPaths.Catalog.ToDictionary(
                kv => kv.Key,
                kv => new { path = kv.Value.Path, found = kv.Value.Found, source = kv.Value.Source });
            return Results.Json(new
            {
                jobs = new object[]
                {
                    new { kind = "substrate-test", label = "Substrate test (guided vs pure)", @default = new { games = "20", depth = "4", mode = "fold", concurrency = "0" } },
                    new { kind = "ladder", label = "Eval overlay ladder", @default = new { games = "20", depth = "4", maxPlies = "160", concurrency = "0" } },
                    new { kind = "tactics", label = "Tactics solve rate", @default = new { depth = "6" } },
                    new { kind = "review", label = "PGN review triage", @default = new { depth = "4", maxGames = "10" } },
                    new { kind = "learned-pst", label = "Learned PST grid", @default = new { piece = "PNBRQK" } },
                    new { kind = "cutechess", label = "cutechess vs Stockfish", @default = new { rounds = "10", st = "1", elo = "2000", depth = "0", concurrency = "1", ingest = "true" } },
                    new { kind = "lichess-fetch", label = "Fetch player PGN", @default = new { site = "lichess" } },
                },
                engines,
            });
        }).WithTags("chess-lab");

        // What the gauntlet WOULD run, resolved against this host's binaries. The form shows
        // it live as the knobs move, so the operator reads the real argv before spending an
        // hour of engine time — and it comes from CutechessRunner.BuildArguments, the same
        // function the job uses, so the preview cannot drift from the thing it previews.
        app.MapGet("/chess/lab/cutechess/preview", (
            int? rounds, int? depth, double? st, int? elo, int? concurrency, bool? limitStrength) =>
        {
            var options = new CutechessOptions
            {
                Rounds = Math.Max(1, rounds ?? 10),
                Depth = Math.Max(0, depth ?? 0),
                SecondsPerMove = Math.Max(0.05, st ?? 1),
                StockfishElo = elo ?? 2000,
                StockfishLimitStrength = limitStrength ?? true,
                Concurrency = Math.Max(1, concurrency ?? 1),
                PgnOut = Path.Combine(ChessLabPaths.LabDir, "{job}", "games.pgn"),
                Event = "chess-lab/cutechess/{job}",
            };

            var catalog = ChessLabPaths.Catalog;
            var required = new (string Name, string Key, string Hint)[]
            {
                ("cutechess", "cutechess", "LAPLACE_CUTECHESS"),
                ("stockfish", "stockfish", "LAPLACE_STOCKFISH"),
                ("qt", "qt", "LAPLACE_QT_BIN"),
                ("laplaceUci", "laplaceUci", "publish the API host — laplace-uci ships beside it"),
            };
            var missing = required
                .Where(r => !catalog[r.Key].Found)
                .Select(r => new { name = r.Name, hint = r.Hint, looked = catalog[r.Key].Path, source = catalog[r.Key].Source })
                .ToArray();

            var args = CutechessRunner.BuildArguments(
                options,
                catalog["laplaceUci"].Path ?? "<laplace-uci>",
                catalog["stockfish"].Path ?? "<stockfish>");
            var command = new ChessLabCommandEvent(
                catalog["cutechess"].Path ?? "<cutechess-cli>", args, ChessLabPaths.LabDir);

            return Results.Json(new
            {
                fileName = command.FileName,
                arguments = args,
                commandLine = command.CommandLine,
                workingDirectory = command.WorkingDirectory,
                games = options.Rounds,
                ready = missing.Length == 0,
                missing,
            });
        }).WithTags("chess-lab");

        app.MapPost("/chess/lab/start", (LabStartRequest req, ChessLabService lab) =>
        {
            if (!Enum.TryParse<ChessLabJobKind>(req.Kind?.Replace("-", ""), ignoreCase: true, out var kind)
                && !TryParseKind(req.Kind, out kind))
                return Results.BadRequest(new { error = $"unknown kind '{req.Kind}'" });
            if (kind == ChessLabJobKind.LichessBot)
                return Results.Conflict(new { error = "managed_service", message = "Use the authenticated lichess service controls; the API must not start a second bot." });
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

        // The raw process transcript, separate from /events on purpose: it replays scrollback
        // to a viewer that arrives late, serves any number of viewers at once, and cannot
        // starve the structured event channel. `after` resumes a dropped connection — the
        // client passes the last seq it rendered and gets everything the ring still holds.
        app.MapGet("/chess/lab/jobs/{jobId}/terminal", async (
            HttpContext ctx, string jobId, long? after, ChessLabService lab, CancellationToken ct) =>
        {
            var terminal = lab.Terminal(jobId);
            if (terminal is null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            // Proxies that buffer an event stream turn a live transcript into a batch report.
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await foreach (var line in terminal.ReadAsync(after ?? -1, ct))
            {
                await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(line, LabEventJson)}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }).WithTags("chess-lab");

        app.MapGet("/chess/lab/jobs/{jobId}/terminal.txt", (string jobId, ChessLabService lab) =>
        {
            // Prefer the on-disk transcript: the in-memory ring is bounded, so for any run
            // long enough to be worth saving it is the truncated copy.
            if (lab.GetJob(jobId)?.Artifacts.TryGetValue("transcript.log", out var file) == true
                && File.Exists(file))
                return Results.File(file, "text/plain; charset=utf-8", $"{jobId}-transcript.log");

            var terminal = lab.Terminal(jobId);
            if (terminal is null) return Results.NotFound();
            var sb = new System.Text.StringBuilder();
            foreach (var line in terminal.Snapshot()) sb.AppendLine(ChessLabTerminal.Format(line));
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        }).WithTags("chess-lab");

        app.MapGet("/chess/lab/jobs/{jobId}/artifact/{name}", (string jobId, string name, ChessLabService lab) =>
        {
            var job = lab.GetJob(jobId);
            if (job is null || !job.Artifacts.TryGetValue(name, out var path) || !File.Exists(path))
                return Results.NotFound();
            // Artifacts are no longer all PGN — a transcript served as x-chess-pgn opens in
            // whatever the browser reserves for chess files instead of as text.
            var contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".pgn" => "application/x-chess-pgn",
                ".log" or ".txt" => "text/plain; charset=utf-8",
                _ => "application/octet-stream",
            };
            return Results.File(path, contentType, name);
        }).WithTags("chess-lab");

        app.MapPost("/chess/lab/jobs/{jobId}/ingest", async (string jobId, ChessLabService lab, CancellationToken ct) =>
        {
            var job = lab.GetJob(jobId);
            if (job is null || !job.Artifacts.TryGetValue("games.pgn", out var path) || !File.Exists(path))
                return Results.NotFound(new { error = "no games.pgn artifact" });
            // Record + analyze the artifact through the writer spine, in-process. Novelty-gated
            // on game ids, so re-posting is idempotent (cutechess jobs already auto-ingest).
            await using var ingestor = await ChessPgnIngestor.CreateAsync(ct);
            var r = await ingestor.IngestFileAsync(path, log: null, ct);
            return Results.Json(new { path, parsed = r.Parsed, ingested = r.Applied, alreadyPresent = r.Parsed - r.Novel });
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
    private sealed record MoveRequest(string Fen, string Uci, string[]? Moves);
    private sealed record EvalRequest(string Fen, int? Depth, bool? Substrate);
    private sealed record ExploreRequest(string Fen, string? Player, int? Limit);
    private sealed record BestMoveRequest(string Fen, double? Temperature, int? Depth, bool? Substrate, string[]? Moves);
    private sealed record LabStartRequest(string? Kind, Dictionary<string, JsonElement>? Config);
    private sealed record LichessStartRequest(int? Depth = null, int? MaxConcurrent = null, bool? Substrate = null, string[]? Speeds = null);
    private sealed record PlayStartRequest(bool? Record, string[]? Moves, string? Tenant, string? User);
    private sealed record PlayMoveRequest(Guid SessionId, string Fen, string Uci);
    private sealed record PlayBestMoveRequest(Guid SessionId, string Fen, int? Depth, bool? Substrate);
    private sealed record PlayFinishRequest(Guid SessionId, string? Status, bool? Adjudicated);
}
