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
        ILogger? log = null)
    {
        _maxDepth = Math.Max(1, maxDepth);
        _host = host;
        _substrate = substrate;
        _record = record;
        _botUsername = botUsername;
        _onChatLine = onChatLine;
        _acceptSpeeds = acceptSpeeds;
        _log = log ?? NullLogger.Instance;
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

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _log.LogInformation("connecting to lichess event stream…");
                await foreach (var ev in StreamNdjsonAsync("/api/stream/event", ct))
                {
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
                            games[gid] = Task.Run(() => PlayGameAsync(gid, weAreWhite, ct), ct);
                        }
                    }

                    foreach (var k in games.Keys.Where(k => games[k].IsCompleted).ToList())
                        games.Remove(k);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "event stream dropped — reconnecting in 10s");
                await Task.Delay(10_000, ct).ConfigureAwait(false);
            }
        }

        if (games.Count > 0)
        {
            _log.LogInformation("draining {N} in-flight games…", games.Count);
            await Task.WhenAll(games.Values).ConfigureAwait(false);
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
            await _host.OpenGameAsync(substrateGameId, "chess/lichess/game", ct: ct);

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
                    stateEl = ev.GetProperty("state");
                    moves = stateEl.TryGetProperty("moves", out var m) ? m.GetString() ?? "" : "";
                    wtime = stateEl.TryGetProperty("wtime", out var wt) ? wt.GetInt32() : 0;
                    btime = stateEl.TryGetProperty("btime", out var bt) ? bt.GetInt32() : 0;
                    winc = stateEl.TryGetProperty("winc", out var wi) ? wi.GetInt32() : 0;
                    binc = stateEl.TryGetProperty("binc", out var bi) ? bi.GetInt32() : 0;
                    initialFen = ev.TryGetProperty("initialFen", out var fen) ? fen.GetString() ?? "startpos" : "startpos";
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
                        search = _host.BuildSearch(_substrate, maxDepth: _maxDepth);
                    }

                    trackedPlies++;
                }

                if (TryParseOutcome(stateEl) is { } parsed)
                {
                    outcome = parsed;
                    break;
                }

                var boardNow = trackState.Board;
                if (boardNow.WhiteToMove != weAreWhite) continue;

                search ??= _host.BuildSearch(_substrate, maxDepth: _maxDepth);

                int myTime = weAreWhite ? wtime : btime;
                int myInc = weAreWhite ? winc : binc;
                int budgetMs = TimeBudget(myTime, myInc);

                var before = trackState;
                search ??= _host.BuildSearch(_substrate, maxDepth: _maxDepth);
                var result = search!.Think(boardNow, new Search.Limits(MaxDepth: _maxDepth, MaxTimeMs: budgetMs), ct);
                if (result.BestMove is not { } mv) continue;

                _log.LogDebug("game {Id}: play {Move} (depth {D}, score {S}cp, budget {B}ms)",
                    lichessGameId, mv.ToUci(), result.Depth, result.Score, budgetMs);

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
                    search = _host.BuildSearch(_substrate, maxDepth: _maxDepth);

                    try
                    {
                        var pv = search!.ExtractPv(boardNow);
                        var motifs = ChessMotifs.DetectAtPly(boardNow, mv, after.Board).ToList();
                        int whiteCp = boardNow.WhiteToMove ? result.Score : -result.Score;
                        string comment = await ChessMoveCommentary.BuildAsync(
                            _host.DataSource,
                            new ChessMoveCommentary.Inputs(whiteCp, result.Depth, pv, motifs),
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
            using var resp = await _http.PostAsync(url, content: null, ct).ConfigureAwait(false);
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
            yield return doc.RootElement;
        }
    }

    public ValueTask DisposeAsync() { _http.Dispose(); return ValueTask.CompletedTask; }
}

public sealed record LichessChatLine(string GameId, string Room, string Username, string Text);
