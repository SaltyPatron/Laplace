using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
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
            await using (var attempt = ReadAttemptAsync(http, path, null, log, ct, receiveTimeout, cleanupTimeout).GetAsyncEnumerator(ct))
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

    internal static async IAsyncEnumerable<JsonElement> ReadAttemptAsync(
        HttpClient http, string path, Action? connected, ILogger log,
        [EnumeratorCancellation] CancellationToken ct,
        TimeSpan? receiveTimeout = null, TimeSpan? cleanupTimeout = null)
    {
        var receiveBudget = receiveTimeout ?? TimeSpan.FromSeconds(30);
        var cleanupBudget = cleanupTimeout ?? TimeSpan.FromSeconds(5);
        if (receiveBudget <= TimeSpan.Zero || cleanupBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(receiveTimeout), "Receive and cleanup budgets must be positive.");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await ReceiveAsync(
            token => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),
            request.Dispose, receiveBudget, cleanupBudget, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw HttpFailure(response);
        }
        connected?.Invoke();
        await using var stream = await ReceiveAsync(
            token => response.Content.ReadAsStreamAsync(token), response.Dispose,
            receiveBudget, cleanupBudget, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // Each blank heartbeat is activity. The receive timer ends before yielding
        // a state, so caller search/recording time is never treated as socket silence.
        while (await ReceiveAsync(token => reader.ReadLineAsync(token).AsTask(), stream.Dispose,
            receiveBudget, cleanupBudget, ct).ConfigureAwait(false) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException)
            {
                // An invalid streamed state cannot safely be omitted before a
                // later terminal event is accepted.
                throw new InvalidDataException("Lichess stream contained malformed NDJSON.");
            }
            using (doc) yield return doc.RootElement;
        }
    }

    private static async Task<T> ReceiveAsync<T>(
        Func<CancellationToken, Task<T>> receive, Action interrupt,
        TimeSpan budget, TimeSpan cleanupBudget, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = receive(lifetime.Token);
        try { return await pending.WaitAsync(budget, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            var cancellation = lifetime.CancelAsync();
            Exception? interruptionError = null;
            try { interrupt(); }
            catch (Exception error) { interruptionError = error; }
            var settlement = Task.WhenAll(cancellation, pending);
            try { await settlement.WaitAsync(cleanupBudget).ConfigureAwait(false); }
            catch (Exception) when (settlement.IsCompleted) { }
            catch (TimeoutException)
            {
                // Do not open another connection while an old receive remains live.
                // Observe eventual failure, and dispose any response arriving late.
                _ = settlement.ContinueWith(static task => { _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                ObserveLateResult(pending);
                throw new LichessStreamCleanupException("Lichess receive did not stop after cancellation and disposal.", ex);
            }
            try { DisposeResult(pending); }
            catch (Exception error) { interruptionError ??= error; }
            if (interruptionError is not null)
                throw new LichessStreamCleanupException("Lichess receive cleanup failed.", interruptionError);
            ct.ThrowIfCancellationRequested();
            throw new IOException("Lichess receive deadline expired or the transport canceled its receive.", ex);
        }
    }

    private static void ObserveLateResult<T>(Task<T> pending)
        => _ = pending.ContinueWith(static task =>
        {
            _ = task.Exception;
            // The session already failed explicitly; a late successful transport
            // result must still release its socket and cannot become a new stream.
            try { DisposeResult(task); }
            catch (Exception) { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static void DisposeResult<T>(Task<T> pending)
    {
        if (pending.Status == TaskStatus.RanToCompletion && pending.Result is IDisposable resource)
            resource.Dispose();
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
