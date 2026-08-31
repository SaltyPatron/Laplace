using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Laplace.Engine.Core;

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
        => Path.Combine(LaplaceInstall.ResolveChessGamesDir(), $"{Sanitize(user)}_{site}.pgn");

    public static Task<int> FetchAsync(
        string user, string site, int? max, int minTcSeconds, string outPath, Action<string>? log, CancellationToken ct)
        => site.ToLowerInvariant() switch
        {
            "lichess" => FetchLichessAsync(user, max, outPath, log, ct),
            "chesscom" or "chess.com" or "chess" => FetchChessComAsync(user, max, minTcSeconds, outPath, log, ct),
            _ => throw new ArgumentException($"unknown site '{site}' (chesscom|lichess)", nameof(site)),
        };

    public static Task<ChessPlayerProfile> FetchProfileAsync(
        string identifier, string site, CancellationToken ct)
        => site.ToLowerInvariant() switch
        {
            "lichess" => FetchLichessProfileAsync(identifier, ct),
            "chesscom" or "chess.com" or "chess" => FetchChessComProfileAsync(identifier, ct),
            "fide" => FetchFideProfileAsync(identifier, ct),
            _ => throw new ArgumentException($"unknown player profile site '{site}' (lichess|chesscom|fide)", nameof(site)),
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
        // Do not materialize a potentially multi-gigabyte all-games export merely to count it.
        // The PGN parser streams the completed file one game at a time.
        return PgnGames.StreamGames(outPath).Count();
    }

    public static async Task<ChessPlayerProfile> FetchLichessProfileAsync(string user, CancellationToken ct)
    {
        string json = await GetStringWithRetryAsync(
            $"https://lichess.org/api/user/{Uri.EscapeDataString(user)}", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string username = String(root, "username") ?? String(root, "id") ?? user;
        JsonElement profile = root.TryGetProperty("profile", out var p) ? p : default;
        var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("perfs", out var perfs))
            foreach (string key in new[] { "standard", "classical", "rapid", "blitz", "bullet" })
                if (perfs.TryGetProperty(key, out var perf) && perf.TryGetProperty("rating", out var rating)
                    && rating.TryGetInt32(out int value))
                    ratings[key] = value;
        if (profile.ValueKind == JsonValueKind.Object
            && profile.TryGetProperty("fideRating", out var fideRating)
            && fideRating.TryGetInt32(out int fideValue))
            ratings["fide"] = fideValue;

        var links = new List<string> { $"https://lichess.org/@/{username}" };
        if (profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("links", out var linkValue))
        {
            if (linkValue.ValueKind == JsonValueKind.String)
                links.AddRange((linkValue.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            else if (linkValue.ValueKind == JsonValueKind.Array)
                links.AddRange(linkValue.EnumerateArray()
                    .Select(v => v.GetString())
                    .OfType<string>()
                    .Where(v => !string.IsNullOrWhiteSpace(v)));
        }

        return new ChessPlayerProfile(
            "lichess", username, username,
            profile.ValueKind == JsonValueKind.Object ? String(profile, "realName") : null,
            profile.ValueKind == JsonValueKind.Object ? String(profile, "bio") : null,
            String(root, "title"),
            profile.ValueKind == JsonValueKind.Object ? String(profile, "country") : null,
            null,
            links.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), ratings);
    }

    public static async Task<ChessPlayerProfile> FetchChessComProfileAsync(string user, CancellationToken ct)
    {
        string escaped = Uri.EscapeDataString(user);
        string json = await GetStringWithRetryAsync($"https://api.chess.com/pub/player/{escaped}", ct);
        string statsJson = await GetStringWithRetryAsync($"https://api.chess.com/pub/player/{escaped}/stats", ct);
        using var doc = JsonDocument.Parse(json);
        using var statsDoc = JsonDocument.Parse(statsJson);
        var root = doc.RootElement;
        string username = String(root, "username") ?? user;
        var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, label) in new[]
        {
            ("chess_daily", "daily"), ("chess_rapid", "rapid"),
            ("chess_blitz", "blitz"), ("chess_bullet", "bullet"),
        })
            if (statsDoc.RootElement.TryGetProperty(key, out var perf)
                && perf.TryGetProperty("last", out var last)
                && last.TryGetProperty("rating", out var rating)
                && rating.TryGetInt32(out int value))
                ratings[label] = value;

        var links = new List<string>();
        if (String(root, "url") is { } url) links.Add(url);
        if (String(root, "country") is { } country) links.Add(country);
        return new ChessPlayerProfile(
            "chesscom", username, username, String(root, "name"), null,
            String(root, "title"), String(root, "country"), null, links, ratings);
    }

    public static async Task<ChessPlayerProfile> FetchFideProfileAsync(string fideId, CancellationToken ct)
    {
        if (!fideId.All(char.IsDigit) || fideId.Length is < 4 or > 12)
            throw new ArgumentException("FIDE identifier must contain 4 to 12 digits.", nameof(fideId));
        string url = $"https://ratings.fide.com/profile/{fideId}";
        string html = await GetStringWithRetryAsync(url, ct);
        return ParseFideProfile(html, fideId, url);
    }

    internal static ChessPlayerProfile ParseFideProfile(string html, string fideId, string url)
    {
        string text = WebText(html);
        string name = HtmlMatch(html, @"<title[^>]*>\s*(.*?)\s+FIDE Profile\s*</title>")
            ?? HtmlMatch(html, @"<h1[^>]*>\s*(.*?)\s*</h1>")
            ?? throw new InvalidDataException("FIDE profile did not contain a player name.");
        var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        AddRating(text, ratings, "standard", @"(\d{3,4})\s+STANDARD\b");
        AddRating(text, ratings, "rapid", @"(\d{3,4})\s+RAPID\b");
        AddRating(text, ratings, "blitz", @"(\d{3,4})\s+BLITZ\b");
        return new ChessPlayerProfile(
            "fide", fideId, name, name, null,
            Match(text, @"FIDE title\s+(.+?)\s+(?:World Rank|Titles|Period)\b"),
            Match(text, @"FIDE ID\s+\d+\s+Federation\s+(.+?)\s+B-Year\b")
                ?? Match(text, @"(?<!Chess )Federation\s+(.+?)\s+(?:B-Year|Gender|FIDE title)\b"),
            fideId, [url], ratings);
    }

    private static string? String(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string WebText(string html)
    {
        string withLines = Regex.Replace(html, @"</(?:div|p|h\d|td|th|li|tr)>", "\n", RegexOptions.IgnoreCase);
        string noTags = Regex.Replace(withLines, "<[^>]+>", " ");
        string decoded = System.Net.WebUtility.HtmlDecode(noTags).Replace('\u00a0', ' ');
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string? Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string? HtmlMatch(string html, string pattern)
    {
        var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;
        string value = Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ");
        return Regex.Replace(System.Net.WebUtility.HtmlDecode(value), @"\s+", " ").Trim();
    }

    private static void AddRating(string text, Dictionary<string, int> ratings, string key, string pattern)
    {
        if (int.TryParse(Match(text, pattern), out int value)) ratings[key] = value;
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

    internal static string Sanitize(string s)
        => new(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
}

public sealed record ChessPlayerProfile(
    string Provider,
    string ProviderId,
    string DisplayName,
    string? RealName,
    string? Biography,
    string? Title,
    string? Federation,
    string? FideId,
    IReadOnlyList<string> Links,
    IReadOnlyDictionary<string, int> Ratings);
