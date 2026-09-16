using System.Text.Json.Nodes;
using Laplace.Ops;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Operator control for starting the canonical CLI ingest lane. The API does not
/// duplicate the CLI source registry or ingest implementation: it starts
/// <c>Laplace.Cli ingest</c> and the normal ingest journal remains the authority
/// for progress, completion and failure.
/// </summary>
internal static class IngestAdminEndpoints
{
    internal sealed record StartRequest(
        string Source,
        string? Path = null,
        string[]? Arguments = null);

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
    }
}
