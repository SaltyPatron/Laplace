using System.Text;
using System.Text.Json;

namespace Laplace.Chess.Service;

/// <summary>
/// Pulls a player's full game history as PGN from the public, sanctioned game APIs (no scraping, no
/// auth): chess.com's Published-Data API and the lichess games API. Writes one .pgn file ready for
/// `ingest chess`. chess.com usernames e.g. "Anthony-Hart", "MagnusCarlsen"; lichess e.g.
/// "DrNykterstein" (Carlsen).
/// </summary>
public static class ChessGameFetcher
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // chess.com rejects empty User-Agent; identify ourselves (their docs ask for a contact).
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Laplace-Chess-Ingest/1.0");
        return c;
    }

    public static string DefaultOut(string user, string site)
        => Path.Combine(
            Environment.GetEnvironmentVariable("LAPLACE_CHESS_GAMES_DIR") ?? @"D:\Data\Ingest\Games\Chess",
            $"{Sanitize(user)}_{site}.pgn");

    public static Task<int> FetchAsync(
        string user, string site, int? max, string outPath, Action<string>? log, CancellationToken ct)
        => site.ToLowerInvariant() switch
        {
            "lichess" => FetchLichessAsync(user, max, outPath, log, ct),
            "chesscom" or "chess.com" or "chess" => FetchChessComAsync(user, max, outPath, log, ct),
            _ => throw new ArgumentException($"unknown site '{site}' (chesscom|lichess)", nameof(site)),
        };

    /// <summary>chess.com Published-Data API: archives index → monthly PGN bundles, concatenated.</summary>
    public static async Task<int> FetchChessComAsync(
        string user, int? max, string outPath, Action<string>? log, CancellationToken ct)
    {
        var archUrl = $"https://api.chess.com/pub/player/{Uri.EscapeDataString(user)}/games/archives";
        log?.Invoke($"chess.com archives: {archUrl}");
        var archJson = await GetStringWithRetryAsync(archUrl, ct);
        using var doc = JsonDocument.Parse(archJson);
        var archives = doc.RootElement.GetProperty("archives").EnumerateArray()
            .Select(e => e.GetString()!).ToList();
        log?.Invoke($"  {archives.Count} monthly archives");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        int games = 0;
        await using (var w = new StreamWriter(outPath, append: false, new UTF8Encoding(false)))
        {
            foreach (var a in archives)
            {
                ct.ThrowIfCancellationRequested();
                var pgn = await GetStringWithRetryAsync($"{a}/pgn", ct);
                if (string.IsNullOrWhiteSpace(pgn)) continue;
                await w.WriteAsync(pgn);
                if (!pgn.EndsWith('\n')) await w.WriteAsync('\n');
                await w.WriteAsync('\n');
                games += CountGames(pgn);
                log?.Invoke($"  {a[^7..]}: {games} games");
                if (max is { } m && games >= m) break;
            }
        }
        return games;
    }

    /// <summary>lichess games API: streams the user's games as PGN directly to the file.</summary>
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
