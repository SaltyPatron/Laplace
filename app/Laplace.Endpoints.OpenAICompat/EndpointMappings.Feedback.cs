using System.Globalization;
using System.Text;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Engine.Core;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Caller confirms or refutes one claim as a runtime witness at <c>UserPromptContent</c>
/// using a canonical <c>.statement</c> record admitted via <c>laplace witness</c>, and
/// reads prior/posterior standing directly from <c>public.laplace_standings</c>.
/// </summary>
internal static class FeedbackEndpoints
{
    private const string TrustClass = "UserPromptContent";

    internal static string ActorLineageName(string tenantId) => $"feedback-actor:{tenantId}";

    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/feedback", async (
            HttpRequest request,
            SubstrateClient substrate,
            ITenantResolver tenants,
            IOptions<LaplaceAuthOptions> auth,
            CancellationToken ct) =>
        {
            var tenant = await tenants.ResolveAsync(request.HttpContext, ct);
            if (tenant.AuthKind == "invalid_key")
                return Unauthorized("invalid_api_key", "The provided API key is unknown, malformed, or revoked.");
            if (string.Equals(tenant.AuthKind, "anonymous", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(tenant.TenantId)
                || (auth.Value.RequiresIdentity
                    && tenant.AuthKind is not ("api_key" or "browser" or "dev")
                    && !OperatorAuth.IsAuthorized(request, auth.Value)))
            {
                return Unauthorized("authentication_required",
                    "Authenticated actor identity (api_key, browser, operator, or dev) is required to submit feedback.");
            }

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

            string participant = tenant.Claims.TryGetValue("user_id", out var user) && Guid.TryParse(user, out var id)
                ? id.ToString("D")
                : tenant.TenantId;
            if (!ConversationContent.IsValidIdentifier(participant)
                || !ConversationContent.IsValidIdentifier(tenant.TenantId))
                return EndpointJson.BadRequest("invalid_tenant", "Tenant id must match [A-Za-z0-9._@-]{1,128}.");

            string objText = payload.Object ?? "";
            Hash128 claimId = ComputeStatementClaimId(payload.Subject, payload.Relation, objText);
            string witnessIdHex = Hash128.Text(participant).ToHex();
            string claimIdHex = claimId.ToHex();
            string actorLineageName = ActorLineageName(tenant.TenantId);
            string actorLineageHex = Hash128.Text(actorLineageName).ToHex();
            request.HttpContext.Response.Headers["X-Laplace-Lineage"] = actorLineageHex;

            FeedbackStanding? before;
            FeedbackStanding? after;
            try
            {
                before = await ReadStandingAsync(substrate.DataSource, claimId, ct);

                var workDir = Path.Combine(Path.GetTempPath(), $"laplace-fb-{Guid.NewGuid():N}");
                Directory.CreateDirectory(workDir);
                try
                {
                    var stmtPath = Path.Combine(workDir, "feedback.statement");
                    string score = verdict == "confirm" ? "1.0" : "0.0";
                    string line = string.Create(CultureInfo.InvariantCulture,
                        $"{SanitizeField(payload.Subject)}\t{SanitizeField(payload.Relation)}\t{SanitizeField(objText)}\t{score}\n");
                    await File.WriteAllTextAsync(stmtPath, line, new UTF8Encoding(false), ct);
                    await NativeEngineCli.RunWitnessAsync(
                        participant, TrustClass, workDir, [stmtPath], actorLineageName, ct);
                }
                finally
                {
                    try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
                }

                after = await ReadStandingAsync(substrate.DataSource, claimId, ct);
            }
            catch (NpgsqlException ex)
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
                ClaimObject: objText,
                WitnessId: witnessIdHex,
                ClaimId: claimIdHex,
                StandingBefore: before,
                StandingAfter: after));
        })
        .WithTags("feedback")
        .Accepts<FeedbackRequest>("application/json")
        .Produces<FeedbackResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult Unauthorized(string code, string message) =>
        Results.Json(
            new ErrorResponse(new ErrorBody("authentication_error", code, message)),
            statusCode: StatusCodes.Status401Unauthorized);

    private static string SanitizeField(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static Hash128 ComputeStatementClaimId(string subject, string relation, string obj)
    {
        var subjId = ResolveAtomOrTextId(subject);
        var relId = ResolveRelationOrTextId(relation);
        if (string.IsNullOrEmpty(obj))
            return HashComposer.ComputeMerkleId(2, [subjId, relId]);
        var objId = ResolveAtomOrTextId(obj);
        return HashComposer.ComputeMerkleId(2, [subjId, relId, objId]);
    }

    private static Hash128 ResolveAtomOrTextId(string value) =>
        Hash128.TryParseHex(value, out var parsed) ? parsed : Hash128.Text(value);

    private static Hash128 ResolveRelationOrTextId(string relation) =>
        ResolveAtomOrTextId(relation);

    private static async Task<FeedbackStanding?> ReadStandingAsync(
        NpgsqlDataSource dataSource, Hash128 claimId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT s.rating, s.deviation, s.matches, public.laplace_confidence(s.rating, s.deviation, 2::double precision) " +
            "FROM public.laplace_standings(ARRAY[public.blake3_from_bytea($1)]) s LIMIT 1",
            conn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, claimId.ToBytes());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        double rating = reader.GetDouble(0);
        double rd = reader.GetDouble(1);
        long matches = reader.GetInt64(2);
        double confidence = reader.GetDouble(3);
        return new FeedbackStanding(rating, rd, matches, confidence);
    }
}
