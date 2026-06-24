using System.Text.Json;

namespace Laplace.Endpoints.OpenAICompat;







internal static class ServerSentEvents
{
    
    public static void Begin(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    
    public static Task WriteJsonAsync(HttpResponse response, object chunk, CancellationToken ct) =>
        response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n", ct);


    public static Task WriteDoneAsync(HttpResponse response, CancellationToken ct) =>
        response.WriteAsync("data: [DONE]\n\n", ct);

    // Terminal error frame for a stream that already sent its 200 + headers. Once SSE has begun
    // the status line is committed, so a mid-stream substrate failure can't become a 4xx/5xx —
    // it must surface as a data frame the client can see, not a silently truncated connection.
    public static Task WriteErrorAsync(HttpResponse response, string code, string message, CancellationToken ct) =>
        response.WriteAsync(
            $"data: {JsonSerializer.Serialize(new { error = new { type = "substrate_error", code, message } })}\n\n",
            ct);
}
