using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Laplace.Chess.Service;

/// <summary>
/// Owns the lifetime of one game-stream connection at a time. A reconnect yields
/// the server's new gameFull into the same caller-owned replay and live session.
/// It never submits a move, changes accepted history, or infers a game result.
/// </summary>
internal static class LichessGameStream
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(60);

    internal static async IAsyncEnumerable<JsonElement> ReadGameAsync(
        HttpClient http, string path, ILogger log,
        [EnumeratorCancellation] CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? wait = null,
        TimeSpan? receiveTimeout = null, TimeSpan? cleanupTimeout = null)
    {
        wait ??= WaitAsync;
        var backoff = InitialBackoff;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Exception? failure = null;
            bool first = true;
            long connectedAt = Stopwatch.GetTimestamp();
            // Disposal happens before the delay and next request, including when
            // the consumer stops on a terminal result or its writer throws.
            await using (var attempt = LichessBot.ReadStreamAttemptAsync(http, path, null, log, ct, receiveTimeout, cleanupTimeout).GetAsyncEnumerator(ct))
            {
                while (true)
                {
                    bool available;
                    try
                    {
                        available = await attempt.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && Retryable(ex))
                    {
                        failure = ex;
                        break;
                    }
                    if (!available) break;
                    var ev = attempt.Current;
                    if (first)
                    {
                        if (!ev.TryGetProperty("type", out var type) || type.GetString() != "gameFull")
                            throw new InvalidDataException("Each Lichess game stream must start with gameFull.");
                        first = false;
                    }
                    // No consumer/replay/writer exception is inside the retry catch.
                    yield return ev;
                }
            }

            ct.ThrowIfCancellationRequested();
            if (!first && Stopwatch.GetElapsedTime(connectedAt) >= MaximumBackoff)
                backoff = InitialBackoff;
            var delay = RetryDelay(backoff, failure);
            log.LogWarning(failure, "game stream {Path} closed; reconnecting in {Delay:0.#}s",
                path, delay.TotalSeconds);
            await wait(delay, ct).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks));
        }
    }

    internal static HttpRequestException HttpFailure(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is { } date)
            delay = date - DateTimeOffset.UtcNow;
        return new StreamHttpException(response.StatusCode, delay);
    }

    internal static TimeSpan RetryDelay(TimeSpan backoff, Exception? failure)
    {
        var delay = backoff;
        if (failure is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } &&
            delay < MaximumBackoff)
            delay = MaximumBackoff;
        if (failure is StreamHttpException { RetryAfter: { } retryAfter } && retryAfter > delay)
            delay = retryAfter;
        return delay;
    }

    private static bool Retryable(Exception error)
        => error switch
        {
            HttpRequestException request => request.StatusCode is null ||
                request.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                (int)request.StatusCode.Value >= 500,
            IOException when error is not InvalidDataException and not LichessStreamCleanupException => true,
            OperationCanceledException => true, // Caller cancellation is excluded by the catch filter.
            _ => false,
        };

    internal static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        // Retry-After may exceed Task.Delay's single-timer range. Honor it in
        // cancelable chunks without shortening the server's requested wait.
        while (delay > TimeSpan.Zero)
        {
            var chunk = delay < MaximumBackoff ? delay : MaximumBackoff;
            await Task.Delay(chunk, ct).ConfigureAwait(false);
            delay -= chunk;
        }
    }

    private sealed class StreamHttpException(HttpStatusCode status, TimeSpan? retryAfter)
        : HttpRequestException($"Lichess stream returned HTTP {(int)status}.", null, status)
    {
        internal TimeSpan? RetryAfter { get; } = retryAfter;
    }
}

// Both account and per-game callers must stop if an old receive remains live.
internal sealed class LichessStreamCleanupException(string message, Exception inner)
    : IOException(message, inner) { }
