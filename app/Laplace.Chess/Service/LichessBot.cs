using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Chess.Service;

public sealed class LichessBot : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly int _maxDepth;
    private readonly ChessLiveGameHost _host;
    private readonly bool _substrate;
    private readonly bool _record;
    private string? _botUsername;
    private readonly Action<LichessChatLine>? _onChatLine;
    private readonly IReadOnlySet<string>? _acceptSpeeds;
    private readonly ILogger _log;
    private readonly Action<bool>? _onConnectionChanged;

    private const string Base = "https://lichess.org";

    public LichessBot(
        string token,
        ChessLiveGameHost host,
        bool substrate = true,
        bool record = true,
        int maxDepth = 8,
        string? botUsername = null,
        Action<LichessChatLine>? onChatLine = null,
        IReadOnlySet<string>? acceptSpeeds = null,
        ILogger? log = null,
        Action<bool>? onConnectionChanged = null)
    {
        _maxDepth = Math.Max(1, maxDepth);
        _host = host;
        _substrate = substrate;
        _record = record;
        _botUsername = botUsername;
        _onChatLine = onChatLine;
        _acceptSpeeds = acceptSpeeds;
        _log = log ?? NullLogger.Instance;
        _onConnectionChanged = onConnectionChanged;
        _http = new HttpClient { BaseAddress = new Uri(Base), Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static string? ResolveToken(string? explicitToken = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitToken)) return explicitToken.Trim();
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
            await DrainGamesAsync(games.Values, gameLifetime, TimeSpan.FromSeconds(20), _log)
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

    private async Task PlayGameAsync(string lichessGameId, bool weAreWhite, CancellationToken ct)
    {
        var modality = new ChessModality();
        var substrateGameId = ChessLiveGameHost.LichessGameId(lichessGameId);
        LichessGameReplay? replay = null;
        GameOutcome? outcome = null;
        Search? search = null;

        if (_record)
            await _host.OpenGameAsync(
                substrateGameId, "chess/lichess/game",
                metadata: new ChessLiveGameMetadata(
                    Event: "Lichess",
                    Site: "lichess.org",
                    ExternalGameId: $"lichess:{lichessGameId}"),
                ct: ct);

        // The compiler emits a finally for this scope: every unscored, canceled
        // or failed stream releases only its own live session, without completion.
        using var gameScope = _record ? _host.CaptureGameScope(substrateGameId) : null;

        try
        {
            await foreach (var ev in LichessGameStream.ReadGameAsync(
                _http, $"/api/bot/game/stream/{lichessGameId}", _log, ct))
            {
                var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "chatLine")
                {
                    string room = ev.TryGetProperty("room", out var r) ? r.GetString() ?? "player" : "player";
                    string user = ev.TryGetProperty("username", out var u) ? u.GetString() ?? "?" : "?";
                    string text = ev.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    _onChatLine?.Invoke(new LichessChatLine(lichessGameId, room, user, text));
                    continue;
                }

                if (type is not ("gameFull" or "gameState")) continue;

                JsonElement stateEl;
                int wtime, btime, winc, binc;
                string initialFen;

                if (type == "gameFull")
                {
                    var sourceWhite = ReadPlayerName(ev, "white") ?? "Lichess player";
                    var sourceBlack = ReadPlayerName(ev, "black") ?? "Lichess player";
                    var whiteName = weAreWhite ? (_botUsername ?? sourceWhite) : sourceWhite;
                    var blackName = weAreWhite ? sourceBlack : (_botUsername ?? sourceBlack);
                    _host.SetGamePlayers(
                        substrateGameId,
                        weAreWhite ? ChessVocabulary.LaplacePlayerId : ChessVocabulary.PlayerId(whiteName), whiteName,
                        weAreWhite ? ChessVocabulary.PlayerId(blackName) : ChessVocabulary.LaplacePlayerId, blackName);
                    stateEl = ev.GetProperty("state");
                    wtime = stateEl.TryGetProperty("wtime", out var wt) ? wt.GetInt32() : 0;
                    btime = stateEl.TryGetProperty("btime", out var bt) ? bt.GetInt32() : 0;
                    winc = stateEl.TryGetProperty("winc", out var wi) ? wi.GetInt32() : 0;
                    binc = stateEl.TryGetProperty("binc", out var bi) ? bi.GetInt32() : 0;
                    initialFen = ev.TryGetProperty("initialFen", out var fen) ? fen.GetString() ?? "startpos" : "startpos";
                    var startFenMeta = initialFen is "startpos" or "" ? ChessModality.StartFen : initialFen;
                    _host.SetGameMetadata(substrateGameId, new ChessLiveGameMetadata(
                        Event: "Lichess",
                        Site: "lichess.org",
                        Date: ReadCreatedDate(ev),
                        TimeControl: ReadTimeControl(ev),
                        TimeControlClass: ReadSpeedClass(ev),
                        StartFen: startFenMeta,
                        ExternalGameId: $"lichess:{lichessGameId}",
                        WhiteRating: ReadPlayerRating(ev, "white"),
                        BlackRating: ReadPlayerRating(ev, "black")));
                }
                else
                {
                    stateEl = ev;
                    wtime = ev.TryGetProperty("wtime", out var wt) ? wt.GetInt32() : 0;
                    btime = ev.TryGetProperty("btime", out var bt) ? bt.GetInt32() : 0;
                    winc = ev.TryGetProperty("winc", out var wi) ? wi.GetInt32() : 0;
                    binc = ev.TryGetProperty("binc", out var bi) ? bi.GetInt32() : 0;
                    initialFen = "startpos";
                }

                var startFen = initialFen is "startpos" or "" ? ChessModality.StartFen : initialFen;
                if (replay is null)
                {
                    if (type != "gameFull")
                        throw new InvalidDataException("Lichess game stream did not start with gameFull.");
                    replay = new LichessGameReplay(startFen);
                }
                else if (type == "gameFull" && startFen != replay.InitialFen)
                    throw new InvalidDataException("Lichess initial position changed during replay.");

                var observed = await replay.ObserveAsync(stateEl,
                    (ply, token) => _record
                        ? _host.RecordPlyAsync(substrateGameId, ply.Ply,
                            modality.StateKey(ply.Before), modality.StateKey(ply.After), ply.Uci,
                            PlayerIdForSide(modality.SideToMove(ply.Before), weAreWhite), token)
                        : Task.CompletedTask, ct);

                // These side observations follow a successfully accepted ply. Keep them
                // outside the append callback so replay cannot retry a recorded move if
                // analysis or commentary fails.
                if (_record)
                {
                    foreach (var ply in observed)
                    {
                        var motifs = ChessMotifs.DetectAtPly(ply.Before.Board, ply.Move, ply.After.Board).ToList();
                        var analysis = ply.SubmittedAnalysis is { } submitted
                            ? submitted with { Motifs = motifs }
                            : new ChessLivePlyAnalysis(Motifs: motifs);
                        if (ply.SubmittedAnalysis is not null || motifs.Count > 0)
                            await _host.RecordPlyAnalysisAsync(substrateGameId, ply.Ply, analysis, ct);

                        if (ply.SubmittedAnalysis?.ScoreCpSideToMove is int score)
                        {
                            try
                            {
                                int whiteCp = ply.Before.Board.WhiteToMove ? score : -score;
                                string comment = await ChessMoveCommentary.BuildAsync(
                                    _host.DataSource,
                                    new ChessMoveCommentary.Inputs(whiteCp, analysis.Depth,
                                        analysis.Pv ?? Array.Empty<string>(), motifs,
                                        PositionSurface: modality.StateKey(ply.After)),
                                    ct);
                                if (!string.IsNullOrWhiteSpace(comment))
                                    await PostChatAsync(lichessGameId, "player", comment, ct);
                            }
                            catch (Exception ex) when (!ct.IsCancellationRequested)
                            {
                                _log.LogDebug(ex, "game {Id}: commentary/chat skipped", lichessGameId);
                            }
                        }
                    }

                    if (replay.AcceptedPlies > 0)
                    {
                        // Initial FEN can start with Black; ply parity is not color.
                        int remaining = replay.State.Board.WhiteToMove ? btime : wtime;
                        if (remaining >= 0)
                            await _host.RecordPlyClockAsync(substrateGameId, replay.AcceptedPlies, remaining, ct);
                    }
                }

                if (replay.Disposition.Stopped)
                {
                    if (_record)
                        _host.SetGameMetadata(substrateGameId,
                            new ChessLiveGameMetadata(Termination: replay.Disposition.Status));
                    outcome = replay.Disposition.Outcome;
                    if (outcome is null)
                        _log.LogInformation("game {Id}: stopped with {Status}; no completed result recorded",
                            lichessGameId, replay.Disposition.Status);
                    break;
                }

                var trackState = replay.State;
                var boardNow = trackState.Board;
                if (!replay.Disposition.CanPlay || replay.HasPendingSubmission ||
                    boardNow.WhiteToMove != weAreWhite) continue;

                int myTime = weAreWhite ? wtime : btime;
                int myInc = weAreWhite ? winc : binc;
                int budgetMs = TimeBudget(myTime, myInc);

                ChessMove mv;
                int scoreCp;
                int searchedDepth;
                long searchedNodes;
                IReadOnlyList<string> pv;
                search ??= _host.BuildSearch(_substrate, maxDepth: _maxDepth);
                _host.RefreshSearch(search, _substrate);
                var result = search.Think(
                    trackState, new Search.Limits(MaxDepth: _maxDepth, MaxTimeMs: budgetMs), ct);
                mv = result.BestMove!.Value;
                scoreCp = result.Score;
                searchedDepth = result.Depth;
                searchedNodes = result.Nodes;
                pv = search.ExtractPv(trackState);

                _log.LogDebug(
                    "game {Id}: play {Move} ({Mode}, depth {D}, score {S}cp, budget {B}ms)",
                    lichessGameId, mv.ToUci(), _substrate ? "substrate-guided search" : "classical control",
                    searchedDepth, scoreCp, budgetMs);

                var pending = replay.BeginSubmission(
                    mv, new ChessLivePlyAnalysis(scoreCp, searchedDepth, searchedNodes, pv));
                var submission = await PostAsync($"/api/bot/game/{lichessGameId}/move/{mv.ToUci()}", ct);
                replay.ResolveSubmission(pending, submission);
                // Even HTTP success is only submission evidence. A later gameState
                // must confirm the exact ply before it reaches RecordPlyAsync.

            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "game {Id} stream ended early", lichessGameId); }

        if (_record && outcome is { } gameOutcome)
        {
            try
            {
                await _host.CompleteGameAsync(substrateGameId, gameOutcome, adjudicated: false, ct);
                _log.LogInformation("game {Id} recorded ({Plies} plies, {Result})",
                    lichessGameId, replay!.AcceptedPlies, Describe(gameOutcome));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "game {Id} substrate complete failed", lichessGameId);
            }
        }

        _log.LogInformation("game {Id} finished", lichessGameId);
    }

    private Hash128? PlayerIdForSide(int moverSide, bool weAreWhite)
    {
        bool botMove = (moverSide == 0) == weAreWhite;
        if (botMove) return ChessVocabulary.LaplacePlayerId;
        return null;
    }

    private static string? ReadPlayerName(JsonElement game, string side)
    {
        if (!game.TryGetProperty(side, out var player)) return null;
        foreach (var key in new[] { "name", "username", "id" })
        {
            if (player.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }
        return null;
    }

    internal static int? ReadPlayerRating(JsonElement game, string side)
    {
        if (!game.TryGetProperty(side, out var player)
            || !player.TryGetProperty("rating", out var rating)
            || rating.ValueKind != JsonValueKind.Number
            || !rating.TryGetInt32(out int value)
            || value <= 0)
            return null;
        return value;
    }

    internal static string? ReadCreatedDate(JsonElement game)
    {
        if (!game.TryGetProperty("createdAt", out var created)
            || created.ValueKind != JsonValueKind.Number
            || !created.TryGetInt64(out long millis))
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis)
                .ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static string? ReadTimeControl(JsonElement game)
    {
        if (!game.TryGetProperty("clock", out var clock) || clock.ValueKind != JsonValueKind.Object)
            return null;
        if (!clock.TryGetProperty("initial", out var initialEl)
            || !initialEl.TryGetInt32(out int initialMs)
            || initialMs < 0)
            return null;
        int incrementMs = clock.TryGetProperty("increment", out var incEl)
            && incEl.TryGetInt32(out int inc) ? Math.Max(0, inc) : 0;
        return $"{initialMs / 1000}+{incrementMs / 1000}";
    }

    internal static string? ReadSpeedClass(JsonElement game)
    {
        if (!game.TryGetProperty("speed", out var speed) || speed.ValueKind != JsonValueKind.String)
            return null;
        return speed.GetString() switch
        {
            "ultraBullet" or "bullet" => "bullet",
            "blitz" => "blitz",
            "rapid" => "rapid",
            "classical" or "correspondence" => "classical",
            _ => null,
        };
    }

    public async Task PostChatAsync(string lichessGameId, string room, string text, CancellationToken ct = default)
    {
        text = ChessMoveCommentary.Truncate(text, ChessMoveCommentary.LichessMaxChars);
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["room"] = room,
                ["text"] = text,
            });
            using var resp = await _http.PostAsync($"/api/bot/game/{lichessGameId}/chat", form, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("POST chat {Id} → {Status}", lichessGameId, (int)resp.StatusCode);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "POST chat {Id} failed", lichessGameId);
        }
    }

    private bool ShouldAccept(JsonElement challenge)
    {
        if (!challenge.TryGetProperty("variant", out var v)
            || !v.TryGetProperty("key", out var vk) || vk.GetString() != "standard")
            return false;
        if (_acceptSpeeds is not null && !_acceptSpeeds.Contains(SpeedOf(challenge)))
            return false;
        return true;
    }

    private static string SpeedOf(JsonElement challenge)
        => challenge.TryGetProperty("speed", out var s) ? s.GetString() ?? "" : "";

    private static string Describe(GameOutcome o)
        => o.IsDraw ? "draw" : o.Winner == 0 ? "white wins" : "black wins";

    private static int TimeBudget(int myTimeMs, int myIncMs)
        => Math.Max(50, Math.Min(myTimeMs - 100, myTimeMs / 20 + (int)(myIncMs * 0.85)));

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
            catch (Exception) { }
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
