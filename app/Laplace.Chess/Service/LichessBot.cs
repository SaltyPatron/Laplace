using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Laplace.Chess.Service.Uci;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Chess.Service;

/// <summary>Which challenges the bot accepts, beyond standard chess: the speeds (null: every speed) and whether a rated
/// challenge is taken (false, the default: casual games only, what a bot under test should play).</summary>
public sealed record LichessChallengePolicy(IReadOnlySet<string>? Speeds = null, bool Rated = false)
{
    public static readonly string[] KnownSpeeds = ["ultraBullet", "bullet", "blitz", "rapid", "classical", "correspondence"];
    public bool Accepts(string speed, bool rated) => (Speeds is null || Speeds.Contains(speed)) && (Rated || !rated);
    public override string ToString() => $"speeds {(Speeds is null ? "all" : string.Join('+', Speeds))}, {(Rated ? "rated and casual" : "casual only")}";
}

public sealed partial class LichessBot : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ChessLiveGameHost _host;
    private ILichessEngine? _engine;
    private readonly bool _record;
    private string? _botUsername;
    private readonly Action<LichessChatLine>? _onChatLine;
    private readonly LichessChallengePolicy _policy;
    private readonly ILogger _log;
    private readonly Action<bool>? _onConnectionChanged;
    private readonly Action<EngineIdentity>? _onEngineIdentity;
    private readonly TimeSpan _drainGrace;

    private const string Base = "https://lichess.org";

    /// <param name="engine">The engine every move comes from (null: LAPLACE_LICHESS_ENGINE, laplace-uci by default),
    /// opened once per game through the generic UCI client. Game recording does not depend on which engine it is.</param>
    public LichessBot(
        string token,
        ChessLiveGameHost host,
        ILichessEngine? engine = null,
        bool record = true,
        string? botUsername = null,
        Action<LichessChatLine>? onChatLine = null,
        LichessChallengePolicy? policy = null,
        ILogger? log = null,
        Action<bool>? onConnectionChanged = null,
        Action<EngineIdentity>? onEngineIdentity = null,
        TimeSpan? drainGrace = null)
    {
        _host = host;
        _drainGrace = drainGrace ?? TimeSpan.FromSeconds(20);
        _engine = engine;
        _onEngineIdentity = onEngineIdentity;
        _record = record;
        _botUsername = botUsername;
        _onChatLine = onChatLine;
        _policy = policy ?? new LichessChallengePolicy();
        _log = log ?? Laplace.Ops.LaplaceLogging.CreateLogger(typeof(LichessBot).FullName!);
        _onConnectionChanged = onConnectionChanged;
        _http = new HttpClient { BaseAddress = new Uri(Base), Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static string? ResolveToken(string? explicitToken = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitToken)) return explicitToken.Trim();
        var file = Environment.GetEnvironmentVariable("LICHESS_TOKEN_FILE");
        if (!string.IsNullOrWhiteSpace(file)) return File.ReadAllText(file).Trim();
        return LaplaceInstall.TryReadConfig("LICHESS_API", "lichess.env")
            ?? LaplaceInstall.TryReadConfig("LICHESS_TOKEN", "lichess.env");
    }

    public static async Task<string?> FetchUsernameAsync(string token, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(Base), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.GetAsync("/api/account", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null;
    }

    public async Task RunAsync(int maxConcurrent = 4, CancellationToken ct = default)
    {
        var token = _http.DefaultRequestHeaders.Authorization?.Parameter ?? "";
        var account = await LichessAccountReadiness.CheckAsync(_http, token, ct).ConfigureAwait(false);
        await RunVerifiedAsync(account, maxConcurrent, ct).ConfigureAwait(false);
    }

    internal async Task RunVerifiedAsync(
        LichessAccountReadiness account, int maxConcurrent, CancellationToken ct,
        Func<CancellationToken, IAsyncEnumerable<JsonElement>>? accountStream = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        if (!account.Ready) throw new InvalidOperationException(account.Error ?? "Lichess BOT account access has not been verified.");
        _botUsername = account.Username;
        accountStream ??= token => StreamNdjsonAsync("/api/stream/event", token);
        wait ??= LichessGameStream.WaitAsync;
        LichessStreamCleanupException? terminalFailure = null;
        var games = new Dictionary<string, Task>();
        using var gameLifetime = new CancellationTokenSource();
        var backoff = TimeSpan.FromSeconds(1);
        var backoffMax = TimeSpan.FromSeconds(60);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _log.LogInformation("connecting to lichess event stream…");
                await foreach (var ev in accountStream(ct))
                {
                    backoff = TimeSpan.FromSeconds(1);
                    var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;

                    if (type == "challenge")
                    {
                        var ch = ev.GetProperty("challenge");
                        string cid = ch.GetProperty("id").GetString()!;
                        int active = games.Values.Count(g => !g.IsCompleted);
                        if (active < maxConcurrent && ShouldAccept(ch))
                        {
                            _log.LogInformation("accepting challenge {Id} ({Speed})", cid, SpeedOf(ch));
                            await PostAsync($"/api/challenge/{cid}/accept", ct);
                        }
                        else
                        {
                            string why = active >= maxConcurrent ? "too many games" : "variant/speed filter";
                            _log.LogInformation("declining challenge {Id}: {Why}", cid, why);
                            await PostAsync($"/api/challenge/{cid}/decline?reason=later", ct);
                        }
                    }
                    else if (type == "gameStart")
                    {
                        var game = ev.GetProperty("game");
                        string gid = game.GetProperty("gameId").GetString()!;
                        bool weAreWhite = game.TryGetProperty("color", out var col)
                            && col.GetString() == "white";
                        if (!games.TryGetValue(gid, out var existing) || existing.IsCompleted)
                        {
                            if (existing is not null)
                                await ObserveGameTaskAsync(existing, _log).ConfigureAwait(false);
                            _log.LogInformation("game {Id} started, we are {Color}", gid, weAreWhite ? "white" : "black");
                            games[gid] = Task.Run(() => PlayGameAsync(gid, weAreWhite, gameLifetime.Token));
                        }
                    }

                    foreach (var k in games.Keys.Where(k => games[k].IsCompleted).ToList())
                    {
                        await ObserveGameTaskAsync(games[k], _log).ConfigureAwait(false);
                        games.Remove(k);
                    }
                }
                if (!ct.IsCancellationRequested) throw new IOException("Lichess event stream closed");
            }
            catch (LichessStreamCleanupException ex)
            {
                terminalFailure = ex;
                _log.LogError(ex, "event stream cleanup failed; stopping before any replacement connection");
                break;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _onConnectionChanged?.Invoke(false);
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
                var delay = LichessGameStream.RetryDelay(backoff, ex) + jitter;
                _log.LogWarning(ex, "event stream dropped — reconnecting in {Delay:0.#}s",
                    delay.TotalSeconds);
                try { await wait(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                backoff = TimeSpan.FromTicks(Math.Min((backoff + backoff).Ticks, backoffMax.Ticks));
            }
            finally { _onConnectionChanged?.Invoke(false); }
        }

        if (games.Count > 0)
        {
            _log.LogInformation("draining {N} in-flight games…", games.Count);
            await DrainGamesAsync(games.Values, gameLifetime, _drainGrace, _log)
                .ConfigureAwait(false);
        }
        if (terminalFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminalFailure).Throw();
    }

    internal static async Task DrainGamesAsync(
        IEnumerable<Task> games, CancellationTokenSource lifetime, TimeSpan grace, ILogger log)
    {
        var completion = Task.WhenAll(games);
        try { await completion.WaitAsync(grace).ConfigureAwait(false); }
        catch (TimeoutException) when (!completion.IsCompleted)
        {
            log.LogWarning("game drain deadline reached; cancelling remaining games (no fabricated outcome)");
            try { await lifetime.CancelAsync().ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "game cancellation callback failed"); }
            await ObserveGameTaskAsync(completion, log).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ObserveGameTaskAsync(completion, log).ConfigureAwait(false);
        }
    }

    private static async Task ObserveGameTaskAsync(Task task, ILogger log)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { log.LogDebug("game task canceled without a completed result"); }
        catch (Exception ex) { log.LogWarning(task.Exception ?? ex, "game task failed without a completed result"); }
    }


    private Task<LichessSubmissionDisposition> PostAsync(string url, CancellationToken ct)
        => SendPostAsync(_http, url, ct, _log);

    internal static async Task<LichessSubmissionDisposition> SendPostAsync(
        HttpClient http, string url, CancellationToken ct, ILogger? log = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await http.PostAsync(url, content: null, reqCts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return LichessSubmissionDisposition.Accepted;
            log?.LogWarning("POST {Url} → {Status}", url, (int)resp.StatusCode);
            if ((int)resp.StatusCode == 429)
            {
                // Keep this attempt pending until the rate-limit wait has elapsed.
                // The next clock snapshot must not immediately trigger another POST.
                var delay = LichessGameStream.RetryDelay(TimeSpan.Zero, LichessGameStream.HttpFailure(resp));
                await (wait ?? LichessGameStream.WaitAsync)(delay, ct).ConfigureAwait(false);
            }
            // A timeout or server failure can follow a committed move. Keep that
            // attempt pending until the stream resolves it instead of resubmitting.
            int status = (int)resp.StatusCode;
            return status >= 400 && status < 500 && status != 408
                ? LichessSubmissionDisposition.Rejected
                : LichessSubmissionDisposition.Unknown;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log?.LogWarning(ex, "POST {Url} unresolved", url);
            return LichessSubmissionDisposition.Unknown;
        }
    }

    // The existing NDJSON transport owner serves both account and game streams.
    // Reconnection policy is separate; parsing and receive cleanup have one body.
    internal static async IAsyncEnumerable<JsonElement> ReadStreamAttemptAsync(
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
            throw LichessGameStream.HttpFailure(response);
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
            catch (Exception failure)
            {
                System.Diagnostics.Trace.TraceWarning("LICHESS_LATE_RESPONSE_DISPOSE_FAILED: {0}", failure);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static void DisposeResult<T>(Task<T> pending)
    {
        if (pending.Status == TaskStatus.RanToCompletion && pending.Result is IDisposable resource)
            resource.Dispose();
    }

    private IAsyncEnumerable<JsonElement> StreamNdjsonAsync(string path, CancellationToken ct)
        => ReadStreamAttemptAsync(_http, path,
            path == "/api/stream/event" ? () => _onConnectionChanged?.Invoke(true) : null, _log, ct);

    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}

public sealed record LichessChatLine(string GameId, string Room, string Username, string Text);
