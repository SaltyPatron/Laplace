using System.Text.Json;

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
        Results.Json(new
        {
            error = new
            {
                type = "invalid_request_error",
                code,
                message
            }
        }, statusCode: StatusCodes.Status400BadRequest);

    public static IResult NotImplemented(string endpoint, string reason) =>
        Results.Json(new
        {
            error = new
            {
                type = "not_implemented",
                code = "stream_e_pending",
                endpoint,
                message = reason
            }
        }, statusCode: StatusCodes.Status501NotImplemented);

    public static IResult ServiceUnavailable(string code, string message) =>
        Results.Json(new
        {
            error = new
            {
                type = "service_unavailable",
                code,
                message
            }
        }, statusCode: StatusCodes.Status503ServiceUnavailable);

    public static IResult PaymentRequired(string code, string message, object? detail = null) =>
        Results.Json(new
        {
            error = new
            {
                type = "payment_required",
                code,
                message,
                detail
            }
        }, statusCode: StatusCodes.Status402PaymentRequired);
}
