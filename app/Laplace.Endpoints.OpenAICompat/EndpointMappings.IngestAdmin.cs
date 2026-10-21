using System.Text.Json.Nodes;
using Laplace.Ops;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Starts and stops <c>laplace ingest</c> processes. Source selection and admission are the
/// CLI's; progress, completion, and failure are read from the ingest journal. Only CLI
/// children started by this server process can be stopped.
/// </summary>
internal static class IngestAdminEndpoints
{
    internal sealed record StartRequest(
        string Source,
        string? Path = null,
        string[]? Arguments = null);

    internal sealed record StopRequest(int Pid);

    public static void MapIngestAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/admin/ingest/sources", (Laplace.Decomposers.Composition.ISeedDecomposerResolver resolver) => Results.Ok(new
        {
            sources = Laplace.Decomposers.Composition.SeedIngestComposition.RegisteredSourceKeys.Concat(resolver.SelectedGenerationKeys)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase),
            scope = "registered_providers", host = Environment.MachineName, cliAvailable = IngestProcessRunner.ResolveCliBinary() is not null
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
    }
}
