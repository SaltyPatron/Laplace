using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Laplace.Agents;

/// <summary>
/// The transport for the external-agent lane: one POST, bounded retries, and a
/// wall-clock deadline the caller sets.
///
/// The client is per-process and long-lived on purpose — a fresh HttpClient per
/// call leaks sockets in TIME_WAIT, and this lane is called from a stdio server
/// that may live for a whole session. Everything that varies per call lives in
/// <see cref="AgentTarget"/> and <see cref="AgentRequest"/>, so one instance serves
/// every provider.
/// </summary>
public sealed class ExternalAgentClient : IDisposable
{
    /// <summary>Retryable by construction: throttling and the transient 5xx family. Never a 4xx.</summary>
    private static readonly HttpStatusCode[] Retryable =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private readonly HttpClient _http;
    private readonly TimeSpan _retryBaseDelay;
    private readonly int _maxAttempts;

    public ExternalAgentClient(
        HttpMessageHandler? handler = null,
        TimeSpan? retryBaseDelay = null,
        int maxAttempts = 3)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        // The deadline is the caller's, enforced by a linked token below. A second
        // ceiling on the HttpClient would cut a legitimately slow model at a limit
        // nobody asked for — reasoning turns on the current frontier models run for
        // minutes.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "laplace-agents/1.0");
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    public async Task<AgentReply> AskAsync(
        AgentTarget target,
        AgentRequest request,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new AgentException("prompt is empty");

        var uri = AgentWireFormat.BuildUri(target);
        var body = AgentWireFormat.BuildBody(target, request).ToJsonString();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var clock = Stopwatch.StartNew();
        Exception? last = null;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                AgentWireFormat.ApplyAuth(message, target);

                response = await _http.SendAsync(message, deadline.Token).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var failure = Describe(target, response.StatusCode, payload);
                    if (attempt < _maxAttempts && Retryable.Contains(response.StatusCode))
                    {
                        last = new AgentException(failure);
                        await DelayAsync(RetryDelay(attempt, response), deadline.Token).ConfigureAwait(false);
                        continue;
                    }

                    throw new AgentException(failure);
                }

                JsonNode? root;
                try { root = JsonNode.Parse(payload); }
                catch (JsonException ex)
                {
                    throw new AgentException(
                        $"{target.Provider.Id} returned a non-JSON body ({(int)response.StatusCode}): " +
                        Redact(AgentWireFormat.Trim(payload), target.ApiKey), ex);
                }

                var (text, finish, input, output, note) = AgentWireFormat.ParseResponse(target, root);
                clock.Stop();
                return new AgentReply(
                    target.Name, target.Provider.Id, target.Model, text,
                    finish, input, output, clock.Elapsed.TotalMilliseconds, attempt, note);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Only the linked deadline can have fired here.
                throw new AgentException(
                    $"{target.Provider.Id}/{target.Model} did not answer within " +
                    $"{timeout.TotalSeconds:0.#}s (attempt {attempt} of {_maxAttempts}). Raise timeout_seconds " +
                    "for a reasoning model — current frontier models routinely run for minutes.", ex);
            }
            catch (HttpRequestException ex)
            {
                last = new AgentException($"{target.Provider.Id} unreachable at {uri}: {ex.Message}", ex);
                if (attempt >= _maxAttempts) throw last;
                await DelayAsync(RetryDelay(attempt, response: null), deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                response?.Dispose();
            }
        }

        throw last ?? new AgentException($"{target.Provider.Id} call failed with no recorded cause");
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The vendor's own Retry-After when it sent one (capped), else fixed backoff.
    /// Uncapped honouring of Retry-After would let a provider park the call past the
    /// caller's deadline; the deadline token cuts it either way, so the cap only
    /// decides whether the last attempt is spent sleeping or asking.
    /// </summary>
    private TimeSpan RetryDelay(int attempt, HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        if (retryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
                return until > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : until;
        }

        return _retryBaseDelay * attempt;
    }

    private static string Describe(AgentTarget target, HttpStatusCode status, string payload)
    {
        string? detail = null;
        try { detail = AgentWireFormat.ExtractError(JsonNode.Parse(payload)); }
        catch (JsonException) { /* a non-JSON error body is reported verbatim below */ }

        var hint = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $" — check the credential in {string.Join(" or ", target.Provider.ApiKeyEnvNames)} " +
                $"(or secrets/{AgentProviders.SecretFile})",
            HttpStatusCode.NotFound =>
                $" — '{target.Model}' is not a model this provider serves at {target.BaseUrl}",
            HttpStatusCode.TooManyRequests => " — rate limited",
            _ => "",
        };

        return $"{target.Provider.Id} returned {(int)status} {status}{hint}: " +
               Redact(AgentWireFormat.Trim(detail ?? payload), target.ApiKey);
    }

    /// <summary>
    /// Vendors quote the rejected credential back in their own 401 text — OpenAI
    /// does it verbatim. Forwarding that unchanged writes the key into the caller's
    /// context, its transcript, and any log downstream of it, from a code path that
    /// only fires when something is already wrong. The key is a value this process
    /// holds, so redacting it is exact rather than a pattern guess.
    /// </summary>
    private static string Redact(string text, string? key) =>
        string.IsNullOrEmpty(key) || key.Length < 4
            ? text
            : text.Replace(key, $"[redacted {key.Length}-char credential]", StringComparison.Ordinal);

    public void Dispose() => _http.Dispose();
}
