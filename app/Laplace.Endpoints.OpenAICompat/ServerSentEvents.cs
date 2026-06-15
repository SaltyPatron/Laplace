using System.Text.Json;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// SSE framing for the streaming inference routes. ONLY the framing lives here — the chunk
/// objects (ChatCompletionChunk / CompletionChunk) are built in the handlers. The serialized
/// bytes are byte-identical to the original inline writes: the same header trio, the same
/// <c>data: {json}\n\n</c> prefix/terminator, and the same <c>data: [DONE]\n\n</c> sentinel.
/// </summary>
internal static class ServerSentEvents
{
    /// <summary>Set the event-stream header trio (content type + cache + proxy-buffering off).</summary>
    public static void Begin(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    /// <summary>Serialize a chunk object and write it as one <c>data: …\n\n</c> SSE frame.</summary>
    public static Task WriteJsonAsync(HttpResponse response, object chunk, CancellationToken ct) =>
        response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n", ct);

    /// <summary>Write the terminal <c>data: [DONE]\n\n</c> sentinel.</summary>
    public static Task WriteDoneAsync(HttpResponse response, CancellationToken ct) =>
        response.WriteAsync("data: [DONE]\n\n", ct);
}
