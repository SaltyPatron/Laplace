using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Laplace.Api.Contracts;
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

    public static void MapIngestAdminEndpoints(this WebApplication app)
    {
        var studio = new RecipeStudioService(app.Logger);

        app.MapGet("/v1/admin/ingest/sources", (WitnessCatalog catalog) => Results.Ok(new
        {
            sources = catalog.Root.Cadence
                .SelectMany(s => s.Sources)
                .Select(s => s.Cli)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
            scope = "manifest_cadence", host = Environment.MachineName, cliAvailable = IngestProcessRunner.ResolveCliBinary() is not null
        })).WithTags("admin");
        app.MapGet("/v1/admin/ingest/processes", () =>
        {
            var rows = IngestProcessRunner.ActiveProcesses()
                .Select(receipt => (JsonNode)new JsonObject
                {
                    ["pid"] = receipt.ProcessId,
                    ["process_run_id"] = receipt.ProcessRunId,
                    ["source"] = receipt.Source,
                    ["path"] = receipt.Path,
                    ["cli"] = receipt.CliPath,
                    ["arguments"] = new JsonArray(receipt.Arguments
                        .Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
                    ["started_at"] = receipt.StartedAt,
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
                    ["started_at"] = receipt.Start.StartedAt,
                    ["ended_at"] = receipt.EndedAt,
                    ["status"] = receipt.Status,
                    ["exit_code"] = receipt.ExitCode,
                    ["output_incomplete"] = receipt.OutputIncomplete,
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
    }

    private static IResult Forbidden(string message) => Results.Json(
        new ErrorResponse(new ErrorBody("authorization_error", "platform_permission_required", message)),
        statusCode: StatusCodes.Status403Forbidden);
}
