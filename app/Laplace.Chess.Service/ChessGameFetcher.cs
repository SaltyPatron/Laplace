using System.Text;
using System.Text.Json;

namespace Laplace.Chess.Service;

public static class ChessGameFetcher
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Laplace-Chess-Ingest/1.0");
        return c;
    }

    public static string DefaultOut(string user, string site)
        => Path.Combine(
            Environment.GetEnvironmentVariable("LAPLACE_CHESS_GAMES_DIR") ?? @"D:\Data\Ingest\Games\Chess",
            $"{Sanitize(user)}_{site}.pgn");

    public static Task<int> FetchAsync(
        string user, string site, int? max, int minTcSeconds, string outPath, Action<string>? log, CancellationToken ct)
        => site.ToLowerInvariant() switch
        {
            "lichess" => FetchLichessAsync(user, max, outPath, log, ct),
            "chesscom" or "chess.com" or "chess" => FetchChessComAsync(user, max, minTcSeconds, outPath, log, ct),
            _ => throw new ArgumentException($"unknown site '{site}' (chesscom|lichess)", nameof(site)),
        };

    public static async Task<int> FetchChessComAsync(
        string user, int? max, int minTcSeconds, string outPath, Action<string>? log, CancellationToken ct)
    {
        var archUrl = $"https://api.chess.com/pub/player/{Uri.EscapeDataString(user)}/games/archives";
        log?.Invoke($"chess.com archives: {archUrl}");
        var archJson = await GetStringWithRetryAsync(archUrl, ct);
        using var doc = JsonDocument.Parse(archJson);
        var archives = doc.RootElement.GetProperty("archives").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        archives.Reverse();
        log?.Invoke($"  {archives.Count} monthly archives (newest first)"
            + (minTcSeconds > 0 ? $", min base TC {minTcSeconds}s" : ""));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        int kept = 0;
        await using (var w = new StreamWriter(outPath, append: false, new UTF8Encoding(false)))
        {
            foreach (var a in archives)
            {
                ct.ThrowIfCancellationRequested();
                var pgn = await GetStringWithRetryAsync($"{a}/pgn", ct);
                if (string.IsNullOrWhiteSpace(pgn)) continue;
                foreach (var game in SplitGames(pgn))
                {
                    if (minTcSeconds > 0 && BaseTcSeconds(game) < minTcSeconds) continue;
                    await w.WriteAsync(game);
                    await w.WriteAsync("\n\n");
                    if (++kept >= (max ?? int.MaxValue)) break;
                }
                log?.Invoke($"  {a[^7..]}: {kept} kept");
                if (max is { } m && kept >= m) break;
            }
        }
        return kept;
    }

    private static IEnumerable<string> SplitGames(string bundle)
    {
        int i = bundle.IndexOf("[Event ", StringComparison.Ordinal);
        while (i >= 0)
        {
            int next = bundle.IndexOf("[Event ", i + 7, StringComparison.Ordinal);
            yield return (next < 0 ? bundle[i..] : bundle[i..next]).Trim();
            i = next;
        }
    }

    private static int BaseTcSeconds(string game)
    {
        const string key = "[TimeControl \"";
        int t = game.IndexOf(key, StringComparison.Ordinal);
        if (t < 0) return 0;
        t += key.Length;
        int end = game.IndexOf('"', t);
        if (end < 0) return 0;
        var tc = game[t..end];
        if (tc.StartsWith("1/", StringComparison.Ordinal)) return int.MaxValue;
        int plus = tc.IndexOf('+');
        var basePart = plus >= 0 ? tc[..plus] : tc;
        return int.TryParse(basePart, out var s) ? s : 0;
    }

    public static async Task<int> FetchLichessAsync(
        string user, int? max, string outPath, Action<string>? log, CancellationToken ct)
    {
        var url = $"https://lichess.org/api/games/user/{Uri.EscapeDataString(user)}";
        if (max is { } m) url += $"?max={m}";
        log?.Invoke($"lichess: {url}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/x-chess-pgn");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(outPath))
            await src.CopyToAsync(dst, ct);
        return CountGames(await File.ReadAllTextAsync(outPath, ct));
    }

    private static async Task<string> GetStringWithRetryAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var resp = await Http.GetAsync(url, ct);
            if ((int)resp.StatusCode == 429 && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }

    private static int CountGames(string pgn)
    {
        int n = 0, i = 0;
        while ((i = pgn.IndexOf("[Event ", i, StringComparison.Ordinal)) >= 0) { n++; i += 7; }
        return n;
    }

    private static string Sanitize(string s)
        => new(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
}
