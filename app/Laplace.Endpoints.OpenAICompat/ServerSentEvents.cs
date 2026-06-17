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
}
