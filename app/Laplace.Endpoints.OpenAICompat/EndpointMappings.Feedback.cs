using Laplace.Api.Contracts;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Gödel-engine feedback lane (doc 15 G3). Confirm/refute deposits go through
/// the SAME implementation as the CLI attest command (FeedbackContent — one
/// implementation per fact) and fold into consensus immediately, so the very
/// next walk reads the updated graph.
/// </summary>
internal static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/feedback", async (HttpRequest request, SubstrateClient substrate, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<FeedbackRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_request_error", "Request body must be JSON.");

            string verdict = payload.Verdict?.Trim().ToLowerInvariant() ?? "";
            if (verdict is not ("confirm" or "refute"))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'verdict' must be 'confirm' or 'refute'.");
            bool confirm = verdict == "confirm";

            bool tripleMode = !string.IsNullOrWhiteSpace(payload.Subject)
                || !string.IsNullOrWhiteSpace(payload.Relation)
                || !string.IsNullOrWhiteSpace(payload.Object);

            try
            {
                CodepointPerfcache.LoadDefault();

                if (tripleMode)
                {
                    if (string.IsNullOrWhiteSpace(payload.Subject)
                        || string.IsNullOrWhiteSpace(payload.Relation)
                        || string.IsNullOrWhiteSpace(payload.Object))
                        return EndpointJson.BadRequest("invalid_request_error",
                            "Triple feedback requires 'subject', 'relation', and 'object'.");
                    return await TripleAsync(substrate, payload.Subject.Trim(),
                        payload.Relation.Trim(), payload.Object.Trim(), verdict, confirm, ct);
                }

                if (payload.Tokens is null || payload.Tokens.Count < 2)
                    return EndpointJson.BadRequest("invalid_request_error",
                        "Chain feedback requires 'tokens' with at least 2 entries (or use subject/relation/object).");
                return await ChainAsync(substrate, payload.Tokens, verdict, confirm, ct);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
            catch (Npgsql.NpgsqlException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("feedback")
        .Accepts<FeedbackRequest>("application/json")
        .Produces<FeedbackResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> ChainAsync(
        SubstrateClient substrate, IReadOnlyList<string> tokens, string verdict, bool confirm,
        CancellationToken ct)
    {
        var cleaned = tokens.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        var resolved = await FeedbackContent.ResolveTokensAsync(substrate.DataSource, cleaned, ct);

        var statuses = new List<FeedbackTokenStatus>(resolved.Count);
        var ids = new List<Hash128>(resolved.Count);
        foreach (var t in resolved)
        {
            if (t.Usable)
            {
                ids.Add(t.Id!.Value);
                statuses.Add(new FeedbackTokenStatus(t.Token, "resolved"));
            }
            else
                statuses.Add(new FeedbackTokenStatus(t.Token, t.Id is null ? "empty" : "unknown_entity"));
        }

        if (ids.Count < 2)
            return EndpointJson.NotFound("entity_not_found",
                $"Need at least 2 tokens with substrate entities for a PRECEDES pair (got {ids.Count}).");

        var result = await FeedbackContent.ApplyAsync(
            substrate.DataSource, FeedbackContent.BuildPrecedesChain(ids, confirm), ct);

        return Results.Json(new FeedbackResponse(
            Object: "laplace.feedback",
            Verdict: verdict,
            Mode: "chain",
            AttestationsInserted: result.AttestationsInserted,
            ConsensusUpdated: result.ConsensusUpdated,
            Tokens: statuses,
            Relation: "PRECEDES"));
    }

    private static async Task<IResult> TripleAsync(
        SubstrateClient substrate, string subject, string relation, string obj,
        string verdict, bool confirm, CancellationToken ct)
    {
        if (!FeedbackContent.TryResolveRelation(relation, out var rel))
            return EndpointJson.BadRequest("invalid_request_error",
                $"'{relation}' is not a canonical relation type (expected e.g. IS_A, PRECEDES, RELATED_TO).");

        var resolved = await FeedbackContent.ResolveTokensAsync(substrate.DataSource, [subject, obj], ct);
        foreach (var t in resolved)
            if (!t.Usable)
                return EndpointJson.NotFound("entity_not_found",
                    $"'{t.Token}' has no substrate entity.");

        Hash128 subjectId = resolved[0].Id!.Value;
        Hash128 objectId = resolved[1].Id!.Value;

        var before = await FeedbackContent.ConsensusStateAsync(
            substrate.DataSource, subjectId, rel.Id, objectId, ct);

        var result = await FeedbackContent.ApplyAsync(
            substrate.DataSource, FeedbackContent.BuildTriple(subjectId, rel.Canonical, objectId, confirm), ct);

        var after = await FeedbackContent.ConsensusStateAsync(
            substrate.DataSource, subjectId, rel.Id, objectId, ct);

        return Results.Json(new FeedbackResponse(
            Object: "laplace.feedback",
            Verdict: verdict,
            Mode: "triple",
            AttestationsInserted: result.AttestationsInserted,
            ConsensusUpdated: result.ConsensusUpdated,
            Tokens:
            [
                new FeedbackTokenStatus(subject, "resolved"),
                new FeedbackTokenStatus(obj, "resolved")
            ],
            Relation: rel.Canonical,
            ConsensusBefore: before is null ? null : new FeedbackConsensusState(before.Rating, before.Rd, before.WitnessCount),
            ConsensusAfter: after is null ? null : new FeedbackConsensusState(after.Rating, after.Rd, after.WitnessCount)));
    }
}
