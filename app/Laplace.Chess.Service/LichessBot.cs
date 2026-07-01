using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Laplace.Chess.Service;

public sealed class LichessBot : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly int _depth;
    private readonly IRootBias? _bias;
    private readonly int[][]? _mgPst;
    private readonly int[][]? _egPst;
    private readonly IReadOnlySet<string>? _acceptSpeeds;
    private readonly ILogger _log;

    private const string Base = "https://lichess.org";

    public LichessBot(
        string token, int depth = 4,
        IRootBias? bias = null, int[][]? mgPst = null, int[][]? egPst = null,
        IReadOnlySet<string>? acceptSpeeds = null, ILogger? log = null)
    {
        _depth = depth;
        _bias = bias;
        _mgPst = mgPst;
        _egPst = egPst;
        _acceptSpeeds = acceptSpeeds;
        _log = log ?? NullLogger.Instance;
        _http = new HttpClient { BaseAddress = new Uri(Base), Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static string? ResolveToken(string? explicitToken = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitToken)) return explicitToken;
        var env = Environment.GetEnvironmentVariable("LICHESS_API");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var envFile = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "deploy", "secrets", "lichess.env");
        if (!File.Exists(envFile)) return null;
        foreach (var line in File.ReadLines(envFile))
        {
            if (line.TrimStart().StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            if (key is "LICHESS_TOKEN" or "LICHESS_API")
                return line[(eq + 1)..].Trim();
        }
        return null;
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

    private async Task PlayGameAsync(string gameId, bool weAreWhite, CancellationToken ct)
    {
        var search = new Search(EvalTerm.All, _bias, ttBits: 20, _mgPst, _egPst);

        try
        {
            await foreach (var ev in StreamNdjsonAsync($"/api/bot/game/stream/{gameId}", ct))
            {
                var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type is not ("gameFull" or "gameState")) continue;

                string moves;
                int wtime, btime, winc, binc;
                string initialFen;

                if (type == "gameFull")
                {
                    var state = ev.GetProperty("state");
                    moves = state.TryGetProperty("moves", out var m) ? m.GetString() ?? "" : "";
                    wtime = state.TryGetProperty("wtime", out var wt) ? wt.GetInt32() : 0;
                    btime = state.TryGetProperty("btime", out var bt) ? bt.GetInt32() : 0;
                    winc = state.TryGetProperty("winc", out var wi) ? wi.GetInt32() : 0;
                    binc = state.TryGetProperty("binc", out var bi) ? bi.GetInt32() : 0;
                    initialFen = ev.TryGetProperty("initialFen", out var fen) ? fen.GetString() ?? "startpos" : "startpos";
                    if (IsTerminal(state)) break;
                }
                else
                {
                    moves = ev.TryGetProperty("moves", out var m) ? m.GetString() ?? "" : "";
                    wtime = ev.TryGetProperty("wtime", out var wt) ? wt.GetInt32() : 0;
                    btime = ev.TryGetProperty("btime", out var bt) ? bt.GetInt32() : 0;
                    winc = ev.TryGetProperty("winc", out var wi) ? wi.GetInt32() : 0;
                    binc = ev.TryGetProperty("binc", out var bi) ? bi.GetInt32() : 0;
                    initialFen = "startpos";
                    if (IsTerminal(ev)) break;
                }

                var startFen = initialFen is "startpos" or "" ? ChessModality.StartFen : initialFen;
                var board = Board.FromFen(startFen);
                foreach (var uci in moves.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    ApplyUci(board, uci);

                if (board.WhiteToMove != weAreWhite) continue;

                int myTime = weAreWhite ? wtime : btime;
                int myInc = weAreWhite ? winc : binc;
                int budgetMs = TimeBudget(myTime, myInc);

                var result = search.Think(board, new Search.Limits(MaxDepth: _depth, MaxTimeMs: budgetMs));
                if (result.BestMove is { } mv)
                {
                    _log.LogDebug("game {Id}: play {Move} (depth {D}, score {S}cp, budget {B}ms)",
                        gameId, mv.ToUci(), result.Depth, result.Score, budgetMs);
                    await PostAsync($"/api/bot/game/{gameId}/move/{mv.ToUci()}", ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "game {Id} stream ended early", gameId); }

        _log.LogInformation("game {Id} finished", gameId);
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

    private static bool IsTerminal(JsonElement state)
    {
        if (!state.TryGetProperty("status", out var s)) return false;
        return s.GetString() is not ("started" or "created");
    }

    private static void ApplyUci(Board board, string uci)
    {
        foreach (var m in MoveGen.Legal(board))
            if (m.ToUci() == uci) { MoveApply.Make(board, m); return; }
    }

    private static int TimeBudget(int myTimeMs, int myIncMs)
        => Math.Max(50, Math.Min(myTimeMs - 100, myTimeMs / 30 + (int)(myIncMs * 0.8)));

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
