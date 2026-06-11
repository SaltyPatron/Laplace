using System.Text.Json;
using Laplace.Api.Contracts;

namespace Laplace.Endpoints.OpenAICompat;

internal static class EndpointJson
{
    public static async Task<T?> ReadJsonAsync<T>(HttpRequest request, CancellationToken ct)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(request.Body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }, ct);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static IResult BadRequest(string code, string message) =>
        Results.Json(
            new ErrorResponse(new ErrorBody("invalid_request_error", code, message)),
            statusCode: StatusCodes.Status400BadRequest);

    public static IResult NotFound(string code, string message) =>
        Results.Json(
            new ErrorResponse(new ErrorBody("not_found", code, message)),
            statusCode: StatusCodes.Status404NotFound);

    public static IResult NotImplemented(string endpoint, string reason) =>
        Results.Json(
            new NotImplementedResponse(new NotImplementedBody("not_implemented", "stream_e_pending", endpoint, reason)),
            statusCode: StatusCodes.Status501NotImplemented);

    public static IResult ServiceUnavailable(string code, string message) =>
        Results.Json(
            new ErrorResponse(new ErrorBody("service_unavailable", code, message)),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    public static IResult PaymentRequired(string code, string message, object? detail = null) =>
        Results.Json(
            new PaymentRequiredResponse(new PaymentRequiredBody("payment_required", code, message, detail)),
            statusCode: StatusCodes.Status402PaymentRequired);
}
