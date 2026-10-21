using System.Text.Json;

namespace Laplace.Endpoints.OpenAICompat;

internal static class ClientDiagnostics
{
    internal sealed record Failure(string? Kind, string? Operation, string? Path, string? RequestId,
        int? Status, string? ErrorType, string? Message, string? Stack);

    public static void MapClientDiagnostics(this WebApplication app)
    {
        // An account/bootstrap failure must still be reportable. This route admits
        // diagnostics only; it has no substrate/account actions or credential access.
        app.MapPost("/diagnostics/client", async (HttpContext http, ILoggerFactory logs) =>
        {
            if (!Auth.ApiKeyEnforcementMiddleware.SameOrigin(http.Request))
                return Results.StatusCode(403);
            if (http.Request.ContentLength > 16 * 1024) return Results.StatusCode(413);
            http.Request.EnableBuffering(bufferThreshold: 16 * 1024, bufferLimit: 16 * 1024);
            Failure? failure;
            try { failure = await http.Request.ReadFromJsonAsync<Failure>(http.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(); }
            catch (IOException) { return Results.StatusCode(413); }
            if (failure is null || string.IsNullOrWhiteSpace(failure.Kind)) return Results.BadRequest();
            using var scope = logs.CreateLogger("Laplace.Browser").BeginScope(new Dictionary<string, object?>
            {
                ["FailedPath"] = Bound(failure.Path?.Split('?', '#')[0], 512),
                ["FailedRequestId"] = Bound(failure.RequestId, 128),
                ["FailedStatus"] = failure.Status,
                ["ClientErrorType"] = Bound(failure.ErrorType, 128),
                ["ClientStack"] = Bound(failure.Stack, 4096),
            });
            logs.CreateLogger("Laplace.Browser").LogWarning(new EventId(1100, "ClientFailure"),
                "Browser {Kind} failure during {Operation}: {ClientMessage}",
                Bound(failure.Kind, 64), Bound(failure.Operation, 128), Bound(failure.Message, 1024));
            return Results.NoContent();
        }).RequireRateLimiting("client-diagnostics").ExcludeFromDescription();
    }

    private static string? Bound(string? value, int length) => value is { Length: > 0 }
        ? value[..Math.Min(value.Length, length)] : null;
}
