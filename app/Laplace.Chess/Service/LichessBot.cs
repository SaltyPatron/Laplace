using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
    private readonly string? _botUsername;
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
        var games = new Dictionary<string, Task>();
        using var gameLifetime = new CancellationTokenSource();
        // Exponential backoff with jitter (GH #493): a lichess outage shouldn't be hammered
        // every fixed 10s, and a one-off blip shouldn't wait a full 10s either. Resets to the
        // floor once a stream delivers an event.
        var backoff = TimeSpan.FromSeconds(1);
        var backoffMax = TimeSpan.FromSeconds(60);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _log.LogInformation("connecting to lichess event stream…");
                await foreach (var ev in StreamNdjsonAsync("/api/stream/event", ct))
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
                            _log.LogInformation("game {Id} started, we are {Color}", gid, weAreWhite ? "white" : "black");
                            games[gid] = Task.Run(() => PlayGameAsync(gid, weAreWhite, gameLifetime.Token));
                        }
                    }

                    foreach (var k in games.Keys.Where(k => games[k].IsCompleted).ToList())
                        games.Remove(k);
                }
                if (!ct.IsCancellationRequested) throw new IOException("Lichess event stream closed");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
                _log.LogWarning(ex, "event stream dropped — reconnecting in {Delay:0.#}s",
                    (backoff + jitter).TotalSeconds);
                try { await Task.Delay(backoff + jitter, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                backoff = TimeSpan.FromTicks(Math.Min((backoff + backoff).Ticks, backoffMax.Ticks));
            }
            finally { _onConnectionChanged?.Invoke(false); }
        }

        if (games.Count > 0)
        {
            _log.LogInformation("draining {N} in-flight games…", games.Count);
            try { await Task.WhenAll(games.Values).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                _log.LogWarning("game drain deadline reached; cancelling remaining games (no fabricated outcome)");
                await gameLifetime.CancelAsync();
                await Task.WhenAll(games.Values).ConfigureAwait(false);
            }
        }
    }

    private async Task PlayGameAsync(string lichessGameId, bool weAreWhite, CancellationToken ct)
    {
        var modality = new ChessModality();
        var substrateGameId = ChessLiveGameHost.LichessGameId(lichessGameId);
        ChessState trackState = modality.Initial();
        bool tracking = false;
        int trackedPlies = 0;
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

        try
        {
            await foreach (var ev in StreamNdjsonAsync($"/api/bot/game/stream/{lichessGameId}", ct))
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
                string moves;
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
                    moves = stateEl.TryGetProperty("moves", out var m) ? m.GetString() ?? "" : "";
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
                    moves = ev.TryGetProperty("moves", out var m) ? m.GetString() ?? "" : "";
                    wtime = ev.TryGetProperty("wtime", out var wt) ? wt.GetInt32() : 0;
                    btime = ev.TryGetProperty("btime", out var bt) ? bt.GetInt32() : 0;
                    winc = ev.TryGetProperty("winc", out var wi) ? wi.GetInt32() : 0;
                    binc = ev.TryGetProperty("binc", out var bi) ? bi.GetInt32() : 0;
                    initialFen = "startpos";
                }

                var startFen = initialFen is "startpos" or "" ? ChessModality.StartFen : initialFen;
                if (!tracking)
                {
                    trackState = modality.FromFen(startFen);
                    tracking = true;
                }

                var uciMoves = moves.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = trackedPlies; i < uciMoves.Length; i++)
                {
                    var board = trackState.Board;
                    ChessMove? applied = null;
                    foreach (var lm in MoveGen.Legal(board))
                    {
                        if (lm.ToUci() == uciMoves[i]) { applied = lm; break; }
                    }
                    if (applied is null)
                    {
                        _log.LogWarning("game {Id}: could not apply move {Uci} at ply {Ply}", lichessGameId, uciMoves[i], i + 1);
                        break;
                    }

                    string fromKey = modality.StateKey(trackState);
                    int mover = modality.SideToMove(trackState);
                    trackState = modality.Apply(trackState, applied.Value);
                    string toKey = modality.StateKey(trackState);
                    int ply = i + 1;

                    if (_record)
                    {
                        Hash128? moverId = PlayerIdForSide(mover, weAreWhite);
                        await _host.RecordPlyAsync(
                            substrateGameId, ply, fromKey, toKey, uciMoves[i], moverId, ct);
                        var motifs = ChessMotifs.DetectAtPly(board, applied.Value, trackState.Board).ToList();
                        if (motifs.Count > 0)
                            await _host.RecordPlyAnalysisAsync(
                                substrateGameId, ply, new ChessLivePlyAnalysis(Motifs: motifs), ct);
                    }

                    trackedPlies++;
                }

                // wtime/btime is the clock AFTER the latest move in this state. It proves the
                // latest ply's remaining clock and nothing about earlier plies after reconnect.
                if (_record && uciMoves.Length > 0 && trackedPlies == uciMoves.Length)
                {
                    int latestPly = uciMoves.Length;
                    bool latestWasWhite = (latestPly & 1) == 1;
                    int remaining = latestWasWhite ? wtime : btime;
                    if (remaining >= 0)
                        await _host.RecordPlyClockAsync(substrateGameId, latestPly, remaining, ct);
                }

                if (TryParseOutcome(stateEl) is { } parsed)
                {
                    if (_record)
                    {
                        string termination = stateEl.TryGetProperty("status", out var status)
                            ? status.GetString() ?? "" : "";
                        _host.SetGameMetadata(substrateGameId,
                            new ChessLiveGameMetadata(Termination: termination));
                    }
                    outcome = parsed;
                    break;
                }

                var boardNow = trackState.Board;
                if (boardNow.WhiteToMove != weAreWhite) continue;

                int myTime = weAreWhite ? wtime : btime;
                int myInc = weAreWhite ? winc : binc;
                int budgetMs = TimeBudget(myTime, myInc);

                var before = trackState;
                ChessMove mv;
                int scoreCp;
                int searchedDepth;
                long searchedNodes;
                IReadOnlyList<string> pv;
                search ??= _host.BuildSearch(_substrate, maxDepth: _maxDepth);
                _host.RefreshSearch(search, _substrate);
                var result = search.Think(
                    boardNow, new Search.Limits(MaxDepth: _maxDepth, MaxTimeMs: budgetMs), ct);
                mv = result.BestMove!.Value;
                scoreCp = result.Score;
                searchedDepth = result.Depth;
                searchedNodes = result.Nodes;
                pv = search.ExtractPv(boardNow);

                _log.LogDebug(
                    "game {Id}: play {Move} ({Mode}, depth {D}, score {S}cp, budget {B}ms)",
                    lichessGameId, mv.ToUci(), _substrate ? "substrate-guided search" : "classical control",
                    searchedDepth, scoreCp, budgetMs);

                await PostAsync($"/api/bot/game/{lichessGameId}/move/{mv.ToUci()}", ct);

                if (_record)
                {
                    string fromKey = modality.StateKey(before);
                    var after = modality.Apply(before, mv);
                    string toKey = modality.StateKey(after);
                    int ply = trackedPlies + 1;
                    await _host.RecordPlyAsync(
                        substrateGameId, ply, fromKey, toKey, mv.ToUci(),
                        ChessVocabulary.LaplacePlayerId, ct);
                    trackState = after;
                    trackedPlies = ply;
                    var motifs = ChessMotifs.DetectAtPly(boardNow, mv, after.Board).ToList();
                    await _host.RecordPlyAnalysisAsync(
                        substrateGameId, ply,
                        new ChessLivePlyAnalysis(scoreCp, searchedDepth, searchedNodes, pv, motifs), ct);

                    try
                    {
                        int whiteCp = boardNow.WhiteToMove ? scoreCp : -scoreCp;
                        string comment = await ChessMoveCommentary.BuildAsync(
                            _host.DataSource,
                            new ChessMoveCommentary.Inputs(whiteCp, searchedDepth, pv, motifs,
                                PositionSurface: toKey),
                            ct);
                        if (!string.IsNullOrWhiteSpace(comment))
                            await PostChatAsync(lichessGameId, "player", comment, ct);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "game {Id}: commentary/chat skipped", lichessGameId);
                    }
                }
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
                    lichessGameId, trackedPlies, Describe(gameOutcome));
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

    private static GameOutcome? TryParseOutcome(JsonElement state)
    {
        if (!state.TryGetProperty("status", out var st)) return null;
        return st.GetString() switch
        {
            "started" or "created" => null,
            "draw" or "stalemate" or "insufficientMaterialClaim" => GameOutcome.Draw,
            _ => state.TryGetProperty("winner", out var w) ? w.GetString() switch
            {
                "white" => GameOutcome.WonBy(0),
                "black" => GameOutcome.WonBy(1),
                _ => GameOutcome.Draw,
            } : GameOutcome.Draw,
        };
    }

    private static string Describe(GameOutcome o)
        => o.IsDraw ? "draw" : o.Winner == 0 ? "white wins" : "black wins";

    private static int TimeBudget(int myTimeMs, int myIncMs)
        => Math.Max(50, Math.Min(myTimeMs - 100, myTimeMs / 20 + (int)(myIncMs * 0.85)));

    private async Task PostAsync(string url, CancellationToken ct)
    {
        try
        {
            // _http carries an infinite overall timeout because the SAME client serves the
            // never-ending NDJSON streams; plain POSTs (accept/decline/move) must not inherit
            // "wait forever" (GH #493) — a hung move POST would silently stall the game loop.
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _http.PostAsync(url, content: null, reqCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                _log.LogWarning("POST {Url} → {Status}", url, (int)resp.StatusCode);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "POST {Url} failed", url);
        }
    }

    private async IAsyncEnumerable<JsonElement> StreamNdjsonAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (path == "/api/stream/event") _onConnectionChanged?.Invoke(true);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (ct.IsCancellationRequested) yield break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { _log.LogDebug("unparseable ndjson line: {Line}", line); continue; }
            using (doc) yield return doc.RootElement;
        }
    }

    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}

public sealed record LichessChatLine(string GameId, string Room, string Username, string Text);
