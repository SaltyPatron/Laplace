using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The structural query surface: shapes and bands are published so a client
/// builds its controls from the substrate, and a read carries its own dials.
/// </summary>
internal static class QueryEndpoints
{
    /// <summary>Shapes served here rather than by the recall responder family.</summary>
    private static readonly string[] NativeShapes =
        ["band_facts", "beam", "path", "neighbors", "generate"];

    public static void MapQueryEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/query/shapes", async (ISubstrateClient substrate, CancellationToken ct) =>
        {
            try
            {
                var published = await substrate.QueryShapesAsync(ct);
                var shapes = published.Concat([
                    new QueryShape("band_facts", "every edge of the topic inside the selected bands", false, false, true),
                    new QueryShape("beam", "beam search over consensus, gated by the band mask", false, true, false),
                    new QueryShape("path", "shortest witnessed path between two topics", true, false, false),
                    new QueryShape("neighbors", "nearest content by position on S³ and trajectory shape", false, false, false),
                    new QueryShape("generate", "seeded trajectory descent from the topic", false, false, false),
                ]).ToList();
                return Results.Json(new QueryShapesResponse("list", shapes));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("query")
        .Produces<QueryShapesResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/query/bands", async (ISubstrateClient substrate, CancellationToken ct) =>
        {
            try
            {
                var bands = await substrate.RelationBandsAsync(ct);
                return Results.Json(new RelationBandsResponse("list", bands));
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("query")
        .Produces<RelationBandsResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/query", async (QueryRequest payload, ISubstrateClient substrate, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(payload.Topic))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'topic' is required.");

            var shape = string.IsNullOrWhiteSpace(payload.Shape) ? "describe" : payload.Shape.Trim();

            try
            {
                var topic = await substrate.ResolveTopicAsync(payload.Topic.Trim(), ct);
                if (topic is null)
                    return EndpointJson.NotFound("topic_not_witnessed",
                        $"No content is witnessed for '{payload.Topic.Trim()}'.");

                (byte[] Id, string Label)? topic2 = null;
                if (!string.IsNullOrWhiteSpace(payload.Topic2))
                {
                    topic2 = await substrate.ResolveTopicAsync(payload.Topic2.Trim(), ct);
                    if (topic2 is null)
                        return EndpointJson.NotFound("topic_not_witnessed",
                            $"No content is witnessed for '{payload.Topic2.Trim()}'.");
                }

                // Context ids disambiguate a sense. They are resolved content,
                // not parsed text — any language reaches the same ids.
                byte[][]? contextIds = null;
                if (payload.Context is { Length: > 0 })
                {
                    var resolved = new List<byte[]>(payload.Context.Length);
                    foreach (var term in payload.Context)
                    {
                        if (string.IsNullOrWhiteSpace(term)) continue;
                        var hit = await substrate.ResolveTopicAsync(term.Trim(), ct);
                        if (hit is not null) resolved.Add(hit.Value.Id);
                    }
                    if (resolved.Count > 0) contextIds = [.. resolved];
                }

                var rows = await substrate.QueryAsync(
                    shape, topic.Value.Id, topic2?.Id, payload.RelationType, payload.Lang,
                    contextIds, payload.Bands, QueryDials.From(payload), ct);

                return Results.Json(new QueryResponse(
                    "query.result", shape,
                    Convert.ToHexString(topic.Value.Id).ToLowerInvariant(), topic.Value.Label,
                    topic2 is null ? null : Convert.ToHexString(topic2.Value.Id).ToLowerInvariant(),
                    topic2?.Label,
                    payload.Bands, rows));
            }
            catch (SubstrateQueryException ex) when (ex.Message.Contains("unknown shape", StringComparison.Ordinal))
            {
                return EndpointJson.BadRequest("invalid_request_error",
                    $"Unknown shape '{shape}'. GET /v1/query/shapes lists what is served.");
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("query")
        .Produces<QueryResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }
}
