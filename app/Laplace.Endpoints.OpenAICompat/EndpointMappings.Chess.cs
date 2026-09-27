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
            Results.Json(await svc.ApplyMoveAsync(req.Fen, req.Uci, req.Moves, ct))).WithTags("chess");

        app.MapPost("/chess/eval", async (EvalRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.EvalPositionAsync(req.Fen, req.Depth ?? 4, req.Substrate ?? true, ct))).WithTags("chess");

        app.MapPost("/chess/bestmove", async (BestMoveRequest req, ChessEngineService svc, CancellationToken ct) =>
            Results.Json(await svc.BestMoveSearchAsync(req.Fen, req.Depth ?? 4, req.Substrate ?? true, req.Moves, ct))).WithTags("chess");

        // Continuations from a position read from MOVE consensus, optionally scoped to a
        // player. The player name is canonicalized by PlayerAlias, as at ingest, so it
        // resolves to the same player id.
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

        // The session's tenant comes from the resolved request authority, not the body,
        // so a recorded game is witnessed only under the caller's own tenant. User is
        // optional caller metadata.
        app.MapPost("/chess/play/start", async (HttpContext ctx, PlayStartRequest req,
            ChessEngineService svc, Auth.ITenantResolver resolver, CancellationToken ct) =>
        {
            var tenant = await resolver.ResolveAsync(ctx, ct);
            return Results.Json(await svc.StartPlaySessionAsync(
                req.Record ?? true, req.Moves, tenant.TenantId, req.User, ct));
        }).WithTags("chess");

        app.MapPost("/chess/play/move", async (HttpContext ctx, PlayMoveRequest req,
            ChessEngineService svc, ChessRuntimeService runtime,
            Auth.ITenantResolver resolver, CancellationToken ct) =>
        {
            var tenant = await resolver.ResolveAsync(ctx, ct);
            var live = await runtime.GetAsync(ct);
            if (!OwnsPlaySession(live.GetPlaySession(req.SessionId), tenant.TenantId))
                return Results.NotFound();
            return Results.Json(await svc.PlayMoveAsync(req.SessionId, req.Fen, req.Uci, ct));
        }).WithTags("chess");

        app.MapPost("/chess/play/bestmove", async (HttpContext ctx, PlayBestMoveRequest req,
            ChessEngineService svc, ChessRuntimeService runtime,
            Auth.ITenantResolver resolver, CancellationToken ct) =>
        {
            var tenant = await resolver.ResolveAsync(ctx, ct);
            var live = await runtime.GetAsync(ct);
            if (!OwnsPlaySession(live.GetPlaySession(req.SessionId), tenant.TenantId))
                return Results.NotFound();
            return Results.Json(await svc.PlayBestMoveAsync(
                req.SessionId, req.Fen, req.Depth ?? 4, req.Substrate ?? true, ct));
        }).WithTags("chess");

        app.MapPost("/chess/play/finish", async (HttpContext ctx, PlayFinishRequest req,
            ChessEngineService svc, ChessRuntimeService runtime,
            Auth.ITenantResolver resolver, CancellationToken ct) =>
        {
            var tenant = await resolver.ResolveAsync(ctx, ct);
            var live = await runtime.GetAsync(ct);
            if (!OwnsPlaySession(live.GetPlaySession(req.SessionId), tenant.TenantId))
                return Results.NotFound();
            await svc.FinishPlaySessionAsync(req.SessionId, req.Status ?? "draw", req.Adjudicated ?? false, ct);
            return Results.Json(new { finished = true });
        }).WithTags("chess");

        app.MapGet("/chess/lichess/games/{gameId}/chat", async (string gameId, ILichessStatusClient lichess, CancellationToken ct) =>
            Results.Json(await lichess.ChatAsync(gameId, ct))).WithTags("chess");

        app.MapGet("/chess/lichess/status", async (ILichessStatusClient lichess, CancellationToken ct) =>
            Results.Json(await lichess.StatusAsync(ct))).WithTags("chess");

        // Starts and stops the managed Lichess service under the /chess/* tenancy and
        // API-key policy; its configuration is server-side only.
        app.MapPost("/chess/lichess/start", async (LichessStartRequest req,
            IServiceControl services, CancellationToken ct) =>
        {
            if (req.Depth is not null || req.MaxConcurrent is not null || req.Substrate is not null || req.Speeds is not null)
                return Results.BadRequest(new { error = "managed_configuration", message = "Set LAPLACE_LICHESS_* server-side and restart the service." });
            return await ServiceControlEndpoints.ExecuteAsync(services, ManagedService.Lichess, ServiceAction.Start, ct);
        }).WithTags("chess");

        app.MapPost("/chess/lichess/stop", async (IServiceControl services, CancellationToken ct) =>
            await ServiceControlEndpoints.ExecuteAsync(services, ManagedService.Lichess, ServiceAction.Stop, ct))
            .WithTags("chess");

        app.MapGet("/chess/lab/calibration", async (CancellationToken ct) =>
            Results.Json(await ChessCalibration.ReadAsync(ct))).WithTags("chess");

        app.MapGet("/chess/lab/catalog", () =>
        {
            var engines = ChessLabPaths.Catalog.ToDictionary(
                kv => kv.Key,
                kv => new { path = kv.Value.Path, found = kv.Value.Found, source = kv.Value.Source });
            return Results.Json(new
            {
                jobs = new object[]
                {
                    new { kind = "substrate-test", label = "Substrate test (guided vs pure)", @default = new { games = "20", depth = "4", mode = "transition", concurrency = "0" } },
                    new { kind = "ladder", label = "Eval overlay ladder", @default = new { games = "20", depth = "4", maxPlies = "160", concurrency = "0" } },
                    new { kind = "tactics", label = "Tactics solve rate", @default = new { depth = "6" } },
                    new { kind = "review", label = "PGN review triage", @default = new { depth = "4", maxGames = "10" } },
                    new { kind = "learned-pst", label = "Learned PST grid", @default = new { piece = "PNBRQK" } },
                    new { kind = "cutechess", label = "cutechess vs Stockfish", @default = new { rounds = "10", st = "1", elo = "2000", depth = "0", concurrency = "1", ingest = "true", stockfishThreads = "", stockfishHashMb = "", stockfishNumaPolicy = "", stockfishSyzygyPath = "" } },
                    new { kind = "lichess-fetch", label = "Ingest player games", @default = new { site = "chesscom", all = "true", max = "1000", ingest = "true" } },
                    new { kind = "player-profile", label = "Acquire and associate player profiles", @default = new { site = "chesscom", ingest = "true" } },
                    new { kind = "fide-search", label = "Search FIDE players", @default = new { limit = "25" } },
                    new { kind = "fide-profile", label = "Import one FIDE profile", @default = new { } },
                    new { kind = "fide-roster", label = "Ingest FIDE top players", @default = new { cohort = "open", limit = "25", ingest = "true" } },
                },
                engines,
            });
        }).WithTags("chess-lab");

        // The argv a cutechess job would run on this host, built by the same
        // CutechessRunner.BuildArguments the job uses.
        app.MapGet("/chess/lab/cutechess/preview", (
            int? rounds, int? depth, double? st, int? elo, int? concurrency, bool? limitStrength,
            string? stockfishThreads, string? stockfishHashMb, string? stockfishNumaPolicy, string? stockfishSyzygyPath) =>
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
            try
            {
                options = options.WithStockfishConfiguration(new Dictionary<string, string>
                {
                    ["stockfishThreads"] = stockfishThreads ?? "",
                    ["stockfishHashMb"] = stockfishHashMb ?? "",
                    ["stockfishNumaPolicy"] = stockfishNumaPolicy ?? "",
                    ["stockfishSyzygyPath"] = stockfishSyzygyPath ?? "",
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

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
                stockfish = new
                {
                    threads = options.StockfishThreads,
                    hashMb = options.StockfishHashMb,
                    numaPolicy = options.StockfishNumaPolicy,
                    syzygyPath = options.StockfishSyzygyPath,
                },
                ready = missing.Length == 0,
                missing,
            });
        }).WithTags("chess-lab");

        // Lab jobs are authorized by the /chess/* tenancy and API-key middleware.
        app.MapPost("/chess/lab/start", (LabStartRequest req, ChessLabService lab) =>
        {
            if (!Enum.TryParse<ChessLabJobKind>(req.Kind?.Replace("-", ""), ignoreCase: true, out var kind)
                && !TryParseKind(req.Kind, out kind))
                return Results.BadRequest(new { error = $"unknown kind '{req.Kind}'" });
            if (kind == ChessLabJobKind.LichessBot)
                return Results.Conflict(new { error = "managed_service", message = "Use the Lichess service controls; the API must not start a second bot." });
            var config = req.Config?.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()) ?? new Dictionary<string, string>();
            if (kind == ChessLabJobKind.Cutechess)
            {
                try { _ = new CutechessOptions().WithStockfishConfiguration(config); }
                catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            }
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
                // camelCase, matching Results.Json elsewhere.
                var json = JsonSerializer.Serialize(evt, evt.GetType(), LabEventJson);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }).WithTags("chess-lab");

        // The raw process transcript, a separate stream from /events: it replays the ring
        // to late viewers and serves any number of them. `after` is the last seq a client
        // rendered; the stream resumes from the next line the ring still holds.
        app.MapGet("/chess/lab/jobs/{jobId}/terminal", async (
            HttpContext ctx, string jobId, long? after, ChessLabService lab, CancellationToken ct) =>
        {
            var terminal = lab.Terminal(jobId);
            if (terminal is null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            // Disables proxy buffering of the event stream.
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await foreach (var line in terminal.ReadAsync(after ?? -1, ct))
            {
                await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(line, LabEventJson)}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }).WithTags("chess-lab");

        app.MapGet("/chess/lab/jobs/{jobId}/terminal.txt", (string jobId, ChessLabService lab) =>
        {
            // The on-disk transcript is complete; the in-memory ring is bounded.
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
            // Content type follows the artifact's extension.
            var contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".pgn" => "application/x-chess-pgn",
                ".json" => "application/json; charset=utf-8",
                ".log" or ".txt" => "text/plain; charset=utf-8",
                _ => "application/octet-stream",
            };
            return Results.File(path, contentType, name);
        }).WithTags("chess-lab");

        app.MapPost("/chess/lab/jobs/{jobId}/ingest", async (string jobId,
            ChessLabService lab, CancellationToken ct) =>
        {
            var job = lab.GetJob(jobId);
            if (job is null || !job.Artifacts.TryGetValue("games.pgn", out var path) || !File.Exists(path))
                return Results.NotFound(new { error = "no games.pgn artifact" });
            if (job.State is ChessLabJobState.Pending or ChessLabJobState.Running)
                return Results.Conflict(new { error = "the retained PGN is still being written" });
            var result = await lab.IngestArtifactAsync(jobId, ct);
            return result is null ? Results.NotFound(new { error = "retained job is no longer available" })
                : Results.Json(result);
        }).WithTags("chess-lab");
    }

    internal static bool OwnsPlaySession(PlaySession? session, string tenantId) =>
        session is not null && string.Equals(session.TenantId, tenantId, StringComparison.Ordinal);

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
        "player-profile" or "playerprofile" => (parsed = ChessLabJobKind.PlayerProfile) == ChessLabJobKind.PlayerProfile,
        "fide-search" or "fidesearch" => (parsed = ChessLabJobKind.FideSearch) == ChessLabJobKind.FideSearch,
        "fide-profile" or "fideprofile" => (parsed = ChessLabJobKind.FideProfile) == ChessLabJobKind.FideProfile,
        "fide-roster" or "fideroster" => (parsed = ChessLabJobKind.FideRoster) == ChessLabJobKind.FideRoster,
        _ => (parsed = default) == default && false,
    };

    private sealed record FenRequest(string Fen);
    private sealed record MoveRequest(string Fen, string Uci, string[]? Moves);
    private sealed record EvalRequest(string Fen, int? Depth, bool? Substrate);
    private sealed record ExploreRequest(string Fen, string? Player, int? Limit);
    private sealed record BestMoveRequest(string Fen, double? Temperature, int? Depth, bool? Substrate, string[]? Moves);
    private sealed record LabStartRequest(string? Kind, Dictionary<string, JsonElement>? Config);
    private sealed record LichessStartRequest(int? Depth = null, int? MaxConcurrent = null, bool? Substrate = null, string[]? Speeds = null);
    private sealed record PlayStartRequest(bool? Record, string[]? Moves, string? User);
    private sealed record PlayMoveRequest(Guid SessionId, string Fen, string Uci);
    private sealed record PlayBestMoveRequest(Guid SessionId, string Fen, int? Depth, bool? Substrate);
    private sealed record PlayFinishRequest(Guid SessionId, string? Status, bool? Adjudicated);
}
