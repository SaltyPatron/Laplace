using System.Globalization;
using System.Text;
using Laplace.Api.Contracts;
using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Caller confirms or refutes one claim as a runtime witness at <c>UserPromptContent</c>
/// using a canonical <c>.statement</c> record admitted via <c>laplace witness</c>, and
/// reads prior/posterior standing directly from <c>public.laplace_consensus</c>.
/// </summary>
internal static class FeedbackEndpoints
{
    private const string TrustClass = "UserPromptContent";

    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/feedback", async (HttpRequest request, SubstrateClient substrate, Auth.ITenantResolver tenants, CancellationToken ct) =>
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
            if (!ConversationContent.IsValidIdentifier(participant))
                return EndpointJson.BadRequest("invalid_tenant", "Tenant id must match [A-Za-z0-9._@-]{1,128}.");

            string objText = payload.Object ?? "";
            Hash128 claimId = ComputeStatementClaimId(payload.Subject, payload.Relation, objText);
            string witnessIdHex = Hash128.Text(participant).ToHex();
            string claimIdHex = claimId.ToHex();

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
                        participant, TrustClass, workDir, [stmtPath], participant, ct);
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
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

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
            "SELECT rating, deviation, matches FROM public.laplace_consensus WHERE claim = $1::public.blake3 LIMIT 1",
            conn);
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, claimId.ToBytes());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        double rating = reader.GetDouble(0);
        double rd = reader.GetDouble(1);
        long matches = reader.GetInt64(2);
        double confidence = Math.Clamp(1.0 / (1.0 + Math.Exp(-(rating - 1500.0) / 173.7178)), 0.0, 1.0);
        return new FeedbackStanding(rating, rd, matches, confidence);
    }
}
