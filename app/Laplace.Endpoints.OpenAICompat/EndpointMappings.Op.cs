using System.Text.Json;
using System.Text.Json.Nodes;
using Laplace.Api.Contracts;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

internal static class OpEndpoints
{
    public static void MapOpEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/op", async (OpRequest payload, ISubstrateClient substrate, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(payload.Name))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'name' is required.");

            Dictionary<string, JsonNode?>? args = null;
            if (payload.Args is { Count: > 0 })
            {
                args = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                foreach (var (k, el) in payload.Args)
                    args[k] = JsonNode.Parse(el.GetRawText());
            }

            try
            {
                var result = await substrate.InvokeOpAsync(
                    payload.Name.Trim(), args, payload.MaxRows ?? InstalledOpInvoker.DefaultRowCap, ct);
                if (result.Error is not null)
                    return EndpointJson.BadRequest("invalid_request_error", result.Error);

                var rows = result.Rows
                    .Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal))
                    .ToList();
                return Results.Json(new OpResponse("op.result", payload.Name.Trim(), rows, result.TruncatedAt));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("op")
        .Produces<OpResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }
}
