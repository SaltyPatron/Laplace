using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Laplace.Api.Contracts;
using Laplace.Data;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Ops;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Starts and stops <c>laplace ingest</c> processes, inspects the corpus estate
/// (<c>recipes/order</c>, <c>source</c>, <c>*.recipe</c>, <c>LAPLACE_DATA</c> staging),
/// runs native <c>--plan</c>, <c>forget</c>, and <c>sweep</c>, and exposes the Recipe Studio
/// linter, auto-drafter, and isolated <c>--claims --no-load</c> packaging inspector.
/// </summary>
internal static class IngestAdminEndpoints
{
    internal sealed record StartRequest(
        string Source,
        string? Path = null,
        string[]? Arguments = null);

    internal sealed record StopRequest(int Pid);

    internal sealed record SourceActionRequest(string Source);

    internal sealed record RecipeLintRequest(
        [property: JsonPropertyName("recipe_dsl")] string RecipeDsl,
        string? Filename = null);

    internal sealed record RecipeInspectRequest(
        string Filename,
        string Sample,
        [property: JsonPropertyName("recipe_dsl")] string RecipeDsl);

    internal sealed record RecipeDraftRequest(
        string Filename,
        string Sample = "");

    internal sealed record RecipeAdmitRequest(
        string Filename,
        string Sample,
        [property: JsonPropertyName("recipe_dsl")] string RecipeDsl,
        [property: JsonPropertyName("source_name")] string? SourceName = null,
        [property: JsonPropertyName("witness_name")] string? WitnessName = null);

    public static void MapIngestAdminEndpoints(this WebApplication app)
    {
        var studio = new RecipeStudioService(app.Logger);

        app.MapGet("/v1/admin/ingest/sources", async (WitnessCatalog catalog, ISubstrateClient substrate, CancellationToken ct) =>
        {
            var estate = studio.GetEstate();
            var liveWitnessCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (substrate is SubstrateClient realSubstrate)
            {
                try
                {
                    await using var conn = await realSubstrate.DataSource.OpenConnectionAsync(ct);
                    foreach (var w in await NpgsqlSubstrateReads.SourceCountsApproxAsync(conn, ct, timeoutSeconds: 4))
                    {
                        if (w.Evidence is not null)
                        {
                            liveWitnessCounts[w.Source] = w.Evidence.Value;
                            if (!string.IsNullOrWhiteSpace(w.IdHex))
                                liveWitnessCounts[w.IdHex] = w.Evidence.Value;
                        }
                    }
                }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
                {
                    // Catalog still renders staged recipes and direct lanes if Postgres is offline.
                }
            }

            var entries = new List<JsonObject>();
            foreach (var entry in estate.Sources)
            {
                long? admitted = null;
                if (!string.IsNullOrWhiteSpace(entry.WitnessId) && liveWitnessCounts.TryGetValue(entry.WitnessId, out var byId))
                    admitted = byId;
                else if (!string.IsNullOrWhiteSpace(entry.Witness) && liveWitnessCounts.TryGetValue(entry.Witness, out var byWitness))
                    admitted = byWitness;
                else if (liveWitnessCounts.TryGetValue(entry.Name, out var byName))
                    admitted = byName;

                entries.Add(new JsonObject
                {
                    ["kind"] = "recipe_source",
                    ["name"] = entry.Name,
                    ["witness"] = entry.Witness,
                    ["witness_id"] = entry.WitnessId,
                    ["staged_on_disk"] = entry.StagedOnDisk,
                    ["stage_status"] = entry.StageStatus,
                    ["matched_path"] = entry.MatchedPath,
                    ["trust_class"] = entry.TrustClass,
                    ["after"] = new JsonArray(entry.After.Select(static a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                    ["recipes_count"] = entry.Recipes.Count,
                    ["admitted_attestations"] = admitted,
                });
            }

            foreach (var lane in new[] { "file", "folder", "code", "gguf", "pgn", "media" })
            {
                entries.Add(new JsonObject
                {
                    ["kind"] = "direct_lane",
                    ["name"] = lane,
                    ["witness"] = null,
                    ["witness_id"] = null,
                    ["staged_on_disk"] = true,
                    ["stage_status"] = "direct_path",
                    ["matched_path"] = null,
                    ["trust_class"] = lane is "code" or "media" or "file" or "folder" ? "UserPromptContent" : "ExternalModel",
                    ["after"] = new JsonArray(),
                    ["recipes_count"] = 0,
                    ["admitted_attestations"] = null,
                });
            }

            var sources = estate.Sources.Select(s => s.Name)
                .Concat(catalog.Root.Cadence.SelectMany(s => s.Sources).Select(s => s.Cli))
                .Concat(new[] { "file", "folder", "code", "gguf", "pgn", "media" })
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Ok(new JsonObject
            {
                ["sources"] = new JsonArray(sources.Select(static s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                ["entries"] = new JsonArray(entries.ToArray()),
                ["data_dir"] = estate.DataDir,
                ["recipes_dir"] = estate.RecipesDir,
                ["scope"] = "estate_and_manifest",
                ["host"] = Environment.MachineName,
                ["cliAvailable"] = IngestProcessRunner.ResolveCliBinary() is not null,
            });
        }).WithTags("admin");
        app.MapGet("/v1/admin/ingest/processes", () =>
        {
            var rows = IngestProcessRunner.ActiveProcessSnapshots()
                .Select(snap =>
                {
                    var receipt = snap.Start;
                    return (JsonNode)new JsonObject
                    {
                        ["pid"] = receipt.ProcessId,
                        ["process_run_id"] = receipt.ProcessRunId,
                        ["source"] = receipt.Source,
                        ["path"] = receipt.Path,
                        ["cli"] = receipt.CliPath,
                        ["arguments"] = new JsonArray(receipt.Arguments
                            .Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
                        ["started_at"] = receipt.StartedAt,
                        ["stdout"] = snap.Stdout,
                        ["stderr"] = snap.Stderr,
                    };
                })
                .ToArray();
            return Results.Json(new JsonObject
            {
                ["object"] = "list",
                ["data"] = new JsonArray(rows),
                ["completed"] = new JsonArray(IngestProcessRunner.CompletedProcesses().Select(receipt => (JsonNode)new JsonObject
                {
                    ["pid"] = receipt.Start.ProcessId,
                    ["process_run_id"] = receipt.Start.ProcessRunId,
                    ["source"] = receipt.Start.Source,
                    ["path"] = receipt.Start.Path,
                    ["cli"] = receipt.Start.CliPath,
                    ["arguments"] = new JsonArray(receipt.Start.Arguments
                        .Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
                    ["started_at"] = receipt.Start.StartedAt,
                    ["ended_at"] = receipt.EndedAt,
                    ["status"] = receipt.Status,
                    ["exit_code"] = receipt.ExitCode,
                    ["output_incomplete"] = receipt.OutputIncomplete,
                    ["stdout"] = receipt.Stdout,
                    ["stderr"] = receipt.Stderr,
                }).ToArray()),
            });
        }).WithTags("admin");

        app.MapPost("/v1/admin/ingest/start", (StartRequest request) =>
        {
            try
            {
                var receipt = IngestProcessRunner.Start(
                    request.Source,
                    request.Path,
                    request.Arguments);
                return Results.Json(new JsonObject
                {
                    ["object"] = "ingest.process",
                    ["pid"] = receipt.ProcessId,
                    ["process_run_id"] = receipt.ProcessRunId,
                    ["source"] = receipt.Source,
                    ["path"] = receipt.Path,
                    ["cli"] = receipt.CliPath,
                    ["arguments"] = new JsonArray(receipt.Arguments
                        .Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
                    ["started_at"] = receipt.StartedAt,
                    ["status"] = "started",
                    ["note"] = "Follow the canonical ingest journal for run progress and completion.",
                });
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_ingest_request", ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                return EndpointJson.BadRequest("ingest_path_not_found", ex.Message);
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Ingest child start failed for source={Source}", request.Source);
                return Results.Problem("The ingest process could not start. See the correlated server log.", statusCode: 503);
            }
        }).WithTags("admin");

        app.MapPost("/v1/admin/ingest/stop", (StopRequest request) =>
        {
            try
            {
                var receipt = IngestProcessRunner.Stop(request.Pid);
                return Results.Json(new JsonObject
                {
                    ["object"] = "ingest.process.stop",
                    ["pid"] = receipt.ProcessId,
                    ["found"] = receipt.Found,
                    ["was_running"] = receipt.WasRunning,
                    ["stop_requested"] = receipt.StopRequested,
                    ["note"] = !receipt.Found
                        ? "This server no longer owns that process. It may have exited already or the server may have restarted."
                        : receipt.StopRequested
                        ? "Stop requested for the CLI process tree. Refresh the canonical journal to observe final run state."
                        : "The owned CLI process had already exited.",
                });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return EndpointJson.BadRequest("invalid_ingest_process", ex.Message);
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Ingest child stop failed for process={ChildProcessId}", request.Pid);
                return Results.Problem("The ingest process could not be stopped. See the correlated server log.", statusCode: 503);
            }
        }).WithTags("admin");

        // ---- Corpus Estate Manager ----------------------------------------

        app.MapGet("/v1/admin/ingest/estate", async (HttpContext http, PlatformAuthorization authorization, CancellationToken ct) =>
        {
            if (!await authorization.AllowsAsync(http, PlatformPolicy.Read, ct))
                return Forbidden("Reading the corpus estate requires platform.read permission.");
            return Results.Ok(studio.GetEstate());
        }).WithTags("admin");

        app.MapPost("/v1/admin/ingest/plan", async (HttpContext http, PlatformAuthorization authorization, CancellationToken ct) =>
        {
            if (!await authorization.AllowsAsync(http, PlatformPolicy.Ingest, ct))
                return Forbidden("Running an ingestion dry-run plan requires ingestion.manage permission.");

            var request = await EndpointJson.ReadJsonAsync<SourceActionRequest>(http.Request, ct);
            if (request is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON with a 'source' field.");

            try
            {
                var cli = await studio.PlanSourceAsync(request.Source, ct);
                return Results.Ok(new
                {
                    @object = "ingest.plan",
                    source = request.Source.Trim(),
                    ok = cli.Ok,
                    exit_code = cli.ExitCode,
                    timed_out = cli.TimedOut,
                    elapsed_ms = cli.ElapsedMs,
                    cli = cli.Cli,
                    arguments = cli.Arguments,
                    stdout = cli.Stdout,
                    stderr = cli.Stderr,
                    output_incomplete = cli.OutputIncomplete,
                });
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_ingest_request", ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                return EndpointJson.ServiceUnavailable("native_cli_unavailable", ex.Message);
            }
        }).WithTags("admin").Accepts<SourceActionRequest>("application/json");

        app.MapPost("/v1/admin/ingest/forget", async (HttpContext http, PlatformAuthorization authorization, CancellationToken ct) =>
        {
            if (!await authorization.AllowsAsync(http, PlatformPolicy.Maintain, ct))
                return Forbidden("Forgetting a source requires platform.maintain permission.");

            var request = await EndpointJson.ReadJsonAsync<SourceActionRequest>(http.Request, ct);
            if (request is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON with a 'source' field.");

            try
            {
                var cli = await studio.ForgetSourceAsync(request.Source, ct);
                return Results.Ok(new
                {
                    @object = "ingest.forget",
                    source = request.Source.Trim(),
                    ok = cli.Ok,
                    exit_code = cli.ExitCode,
                    timed_out = cli.TimedOut,
                    elapsed_ms = cli.ElapsedMs,
                    cli = cli.Cli,
                    arguments = cli.Arguments,
                    stdout = cli.Stdout,
                    stderr = cli.Stderr,
                    output_incomplete = cli.OutputIncomplete,
                });
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_ingest_request", ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                return EndpointJson.ServiceUnavailable("native_cli_unavailable", ex.Message);
            }
        }).WithTags("admin").Accepts<SourceActionRequest>("application/json");

        app.MapPost("/v1/admin/ingest/sweep", async (HttpContext http, PlatformAuthorization authorization, CancellationToken ct) =>
        {
            if (!await authorization.AllowsAsync(http, PlatformPolicy.Maintain, ct))
                return Forbidden("Sweeping unreferenced entities requires platform.maintain permission.");

            try
            {
                var cli = await studio.SweepAsync(ct);
                return Results.Ok(new
                {
                    @object = "ingest.sweep",
                    ok = cli.Ok,
                    exit_code = cli.ExitCode,
                    timed_out = cli.TimedOut,
                    elapsed_ms = cli.ElapsedMs,
                    cli = cli.Cli,
                    arguments = cli.Arguments,
                    stdout = cli.Stdout,
                    stderr = cli.Stderr,
                    output_incomplete = cli.OutputIncomplete,
                });
            }
            catch (FileNotFoundException ex)
            {
                return EndpointJson.ServiceUnavailable("native_cli_unavailable", ex.Message);
            }
        }).WithTags("admin");

        // ---- Interactive Recipe Studio ------------------------------------

        app.MapGet("/v1/recipe-studio/catalog", async (HttpContext http, PlatformAuthorization authorization, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            if (!await authorization.AllowsAsync(http, PlatformPolicy.Read, ct))
                return Forbidden("Reading the Recipe Studio catalog requires platform.read permission.");
            return Results.Ok(studio.GetCatalog());
        }).WithTags("recipe-studio");

        app.MapPost("/v1/recipe-studio/lint", async (HttpContext http, PlatformAuthorization authorization, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            if (!await authorization.AllowsAsync(http, PlatformPolicy.Read, ct))
                return Forbidden("Using Recipe Studio requires platform.read permission.");

            var request = await EndpointJson.ReadJsonAsync<RecipeLintRequest>(http.Request, ct);
            if (request is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON with 'recipe_dsl'.");

            return Results.Ok(studio.LintRecipe(request.RecipeDsl ?? "", request.Filename));
        }).WithTags("recipe-studio").Accepts<RecipeLintRequest>("application/json");

        app.MapPost("/v1/recipe-studio/inspect", async (HttpContext http, PlatformAuthorization authorization, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            if (!await authorization.AllowsAsync(http, PlatformPolicy.Read, ct))
                return Forbidden("Inspecting recipes requires platform.read permission.");

            var request = await EndpointJson.ReadJsonAsync<RecipeInspectRequest>(http.Request, ct);
            if (request is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON with 'filename', 'sample', and 'recipe_dsl'.");

            try
            {
                var result = await studio.InspectAsync(request.Filename ?? "", request.Sample ?? "", request.RecipeDsl ?? "", ct);
                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_recipe_studio_request", ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                return EndpointJson.ServiceUnavailable("native_cli_unavailable", ex.Message);
            }
        }).WithTags("recipe-studio").Accepts<RecipeInspectRequest>("application/json");

        app.MapPost("/v1/recipe-studio/draft", async (HttpContext http, PlatformAuthorization authorization, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            if (!await authorization.AllowsAsync(http, PlatformPolicy.Read, ct))
                return Forbidden("Drafting recipes requires platform.read permission.");

            var request = await EndpointJson.ReadJsonAsync<RecipeDraftRequest>(http.Request, ct);
            if (request is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON with 'filename' and 'sample'.");

            try
            {
                return Results.Ok(studio.DraftRecipe(request.Filename ?? "", request.Sample ?? ""));
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_recipe_studio_request", ex.Message);
            }
        }).WithTags("recipe-studio").Accepts<RecipeDraftRequest>("application/json");

        app.MapPost("/v1/recipe-studio/admit", async (HttpContext http, PlatformAuthorization authorization, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            if (!await authorization.AllowsAsync(http, PlatformPolicy.Ingest, ct))
                return Forbidden("Admitting samples via Recipe Studio requires ingestion.manage permission.");

            var request = await EndpointJson.ReadJsonAsync<RecipeAdmitRequest>(http.Request, ct);
            if (request is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON with 'filename', 'sample', and 'recipe_dsl'.");

            try
            {
                var result = await studio.AdmitAsync(
                    request.Filename ?? "",
                    request.Sample ?? "",
                    request.RecipeDsl ?? "",
                    request.SourceName,
                    request.WitnessName,
                    ct);
                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_recipe_studio_request", ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                return EndpointJson.ServiceUnavailable("native_cli_unavailable", ex.Message);
            }
        }).WithTags("recipe-studio").Accepts<RecipeAdmitRequest>("application/json");
    }

    private static IResult Forbidden(string message) => Results.Json(
        new ErrorResponse(new ErrorBody("authorization_error", "platform_permission_required", message)),
        statusCode: StatusCodes.Status403Forbidden);
}
