using System.Text.Json.Serialization;
using Laplace.Api.Contracts;
using Laplace.Endpoints.OpenAICompat.Auth;
using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

public sealed record ArenaCombineRequest(
    [property: JsonPropertyName("a")] string? A,
    [property: JsonPropertyName("b")] string? B);

public sealed record ArenaHighwayStepRequest(
    [property: JsonPropertyName("current_id")] string? CurrentId,
    [property: JsonPropertyName("next_id")] string? NextId,
    [property: JsonPropertyName("target_id")] string? TargetId,
    [property: JsonPropertyName("step_count")] int StepCount = 0);

public sealed record ArenaVerifyRequest(
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("seed")] string? Seed,
    [property: JsonPropertyName("guess")] string? Guess,
    [property: JsonPropertyName("reveal")] bool Reveal = false);

public sealed record ArenaBlindMatchupRequest(
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("firmware_a")] string? FirmwareA,
    [property: JsonPropertyName("firmware_b")] string? FirmwareB,
    [property: JsonPropertyName("scope")] string? Scope = null,
    [property: JsonPropertyName("witnesses")] IReadOnlyList<string>? Witnesses = null);

public static class ArenaEndpoints
{
    public static void MapArenaEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/arena/combine", async (
            HttpRequest request,
            ArenaService arena,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ArenaCombineRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.A) || string.IsNullOrWhiteSpace(payload.B))
                return EndpointJson.BadRequest("invalid_request_error", "Fields 'a' and 'b' are required.");

            try
            {
                var result = await arena.CombineAsync(payload.A, payload.B, ct);
                return Results.Json(result);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("arena")
        .Accepts<ArenaCombineRequest>("application/json")
        .Produces<ArenaCombineResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/v1/arena/challenge", async (
            string? mode,
            string? seed,
            ArenaService arena,
            CancellationToken ct) =>
        {
            try
            {
                var result = await arena.GetChallengeAsync(mode, seed, ct);
                return Results.Json(result);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("arena")
        .Produces<ArenaChallengeResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/arena/highway-step", async (
            HttpRequest request,
            ArenaService arena,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ArenaHighwayStepRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.CurrentId)
                || string.IsNullOrWhiteSpace(payload.NextId)
                || string.IsNullOrWhiteSpace(payload.TargetId))
                return EndpointJson.BadRequest(
                    "invalid_request_error",
                    "Fields 'current_id', 'next_id', and 'target_id' are required.");

            try
            {
                var result = await arena.HighwayStepAsync(
                    payload.CurrentId, payload.NextId, payload.TargetId, payload.StepCount, ct);
                return result.Valid
                    ? Results.Json(result)
                    : Results.Json(result, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("arena")
        .Accepts<ArenaHighwayStepRequest>("application/json")
        .Produces<ArenaHighwayStepResponse>()
        .Produces<ArenaHighwayStepResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/arena/verify", async (
            HttpRequest request,
            ArenaService arena,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ArenaVerifyRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Mode))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'mode' is required.");
            if (!payload.Reveal && string.IsNullOrWhiteSpace(payload.Guess))
                return EndpointJson.BadRequest("invalid_request_error", "Field 'guess' is required.");

            try
            {
                var result = await arena.VerifyChallengeAsync(
                    payload.Mode, payload.Seed, payload.Guess ?? string.Empty, ct, payload.Reveal);
                return Results.Json(result);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("arena")
        .Accepts<ArenaVerifyRequest>("application/json")
        .Produces<ArenaVerifyResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/v1/arena/blind-matchup", async (
            HttpRequest request,
            ArenaService arena,
            ITenantResolver tenantResolver,
            CancellationToken ct) =>
        {
            var payload = await EndpointJson.ReadJsonAsync<ArenaBlindMatchupRequest>(request, ct);
            if (payload is null)
                return EndpointJson.BadRequest("invalid_json", "Request body must be valid JSON.");
            if (string.IsNullOrWhiteSpace(payload.Prompt)
                || string.IsNullOrWhiteSpace(payload.FirmwareA)
                || string.IsNullOrWhiteSpace(payload.FirmwareB))
                return EndpointJson.BadRequest(
                    "invalid_request_error",
                    "Fields 'prompt', 'firmware_a', and 'firmware_b' are required.");

            var tenantContext = await tenantResolver.ResolveAsync(request.HttpContext, ct);
            var auth = AuthorityScopePolicy.Resolve(
                request,
                tenantContext,
                (uint)LaplaceNative.LP_CAP_READ,
                requestWrite: false,
                explicitScope: payload.Scope,
                explicitWitnesses: payload.Witnesses);
            if (!auth.Allowed)
                return Results.Json(
                    new ErrorResponse(new ErrorBody("authorization_error", auth.ErrorCode!, auth.ErrorMessage!)),
                    statusCode: auth.StatusCode);

            try
            {
                var result = await arena.BlindMatchupAsync(
                    payload.Prompt, payload.FirmwareA, payload.FirmwareB, auth.Scope, ct);
                return Results.Json(result);
            }
            catch (ArgumentException ex)
            {
                return EndpointJson.BadRequest("invalid_request_error", ex.Message);
            }
            catch (SubstrateUnavailableException ex)
            {
                return EndpointJson.ServiceUnavailable("substrate_unavailable", ex.Message);
            }
        })
        .WithTags("arena")
        .Accepts<ArenaBlindMatchupRequest>("application/json")
        .Produces<ArenaBlindMatchupResponse>()
        .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
        .Produces<ErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }
}
