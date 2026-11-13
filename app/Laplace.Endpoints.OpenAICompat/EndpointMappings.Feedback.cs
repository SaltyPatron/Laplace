using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Ingestion;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The caller confirms or refutes one claim. The participant, the authenticated user or else the tenant, is a
/// runtime witness at UserPromptContent: its verdict is a statement the Engine records (FeedbackStatements, the lane
/// MCP feedback and the CLI attest share), and the response carries the claim's Native standing before and after.
/// </summary>
internal static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/feedback", async (HttpRequest request, SubstrateClient substrate, ITenantResolver tenants, CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<FeedbackRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_request_error", "Request body must be JSON.");

            string verdict = payload.Verdict?.Trim().ToLowerInvariant() ?? "";
            if (verdict is not ("confirm" or "refute"))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'verdict' must be 'confirm' or 'refute'.");
            if (string.IsNullOrEmpty(payload.Subject) || string.IsNullOrEmpty(payload.Relation))
                return EndpointJson.BadRequest("invalid_request_error",
                    "Feedback names one claim: 'subject' and 'relation', and 'object' unless the claim has none. "
                    + "Order is what content shows, not a claim: a sequence of tokens is not feedback.");

            var tenant = await tenants.ResolveAsync(request.HttpContext, ct);
            string participant = tenant.Claims.TryGetValue("user_id", out var user) && Guid.TryParse(user, out var id)
                ? id.ToString("D")
                : tenant.TenantId;
            if (!Laplace.Decomposers.Abstractions.ConversationContent.IsValidIdentifier(participant))
                return EndpointJson.BadRequest("invalid_tenant", "Tenant id must match [A-Za-z0-9._@-]{1,128}.");

            FeedbackStatements.Result result;
            try
            {
                result = await FeedbackStatements.StateAsync(RuntimeWitnessRecorder.Installed(), substrate.DataSource,
                    participant, payload.Subject, payload.Relation, payload.Object ?? "", verdict == "confirm", ct);
            }
            catch (Npgsql.NpgsqlException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return EndpointJson.ServiceUnavailable("witness_unavailable", ex.Message);
            }

            return Results.Json(new FeedbackResponse(
                Object: "laplace.feedback",
                Verdict: verdict,
                Participant: participant,
                Subject: payload.Subject,
                Relation: payload.Relation,
                ClaimObject: payload.Object ?? "",
                WitnessId: result.WitnessId,
                ClaimId: result.ClaimId,
                StandingBefore: Standing(result.Before),
                StandingAfter: Standing(result.After)));
        })
        .WithTags("feedback")
        .Accepts<FeedbackRequest>("application/json")
        .Produces<FeedbackResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static FeedbackStanding? Standing(FeedbackStatements.Standing? s)
        => s is null ? null : new FeedbackStanding(s.Rating, s.Deviation, s.Matches, s.Confidence);
}
