using System.Text.Json.Nodes;
using Laplace.Ops;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Operator control for the canonical CLI ingest lane. The API does not duplicate
/// the CLI source registry or ingest implementation: it starts <c>Laplace.Cli ingest</c>
/// and the normal ingest journal remains the authority for progress, completion and failure.
/// Process stop is restricted to CLI children started by this server instance.
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
                    ["source"] = receipt.Source,
                    ["path"] = receipt.Path,
                    ["cli"] = receipt.CliPath,
                    ["arguments"] = new JsonArray(receipt.Arguments
                        .Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
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
                return EndpointJson.BadRequest("ingest_start_failed", ex.Message);
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
                return EndpointJson.BadRequest("ingest_stop_failed", ex.Message);
            }
        }).WithTags("admin");
    }
}
