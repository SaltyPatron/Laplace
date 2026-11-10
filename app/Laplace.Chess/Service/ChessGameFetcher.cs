using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

public static partial class ChessGameFetcher
{
    private static readonly HttpClient Http = CreateClient();
    // Chess.com asks for one request at a time and a User-Agent that names a contact.
    internal const string ContactUserAgent =
        "LaplaceChessIngester/1.0 (https://hartonomous.com; contact: aharttn@gmail.com)";
    private static readonly SemaphoreSlim ChessComRequests = new(1, 1);
    private static readonly SemaphoreSlim LichessRequests = new(1, 1);
    private const int ProviderMaxAttempts = 6;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(ContactUserAgent);
        return c;
    }

    internal readonly record struct ProviderResponse(string Body, string? ETag, bool NotModified);

    internal static string ArchiveDirectory(string user)
    {
        string fallback = Path.Combine(ChessLabPaths.LabDir, "chesscom", Sanitize(user));
        string shared;
        try
        {
            shared = Path.Combine(LaplaceInstall.ResolveChessGamesDir(), "chesscom", Sanitize(user));
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException)
        {
            return fallback;
        }
        // The isolated API runtime identity may not own the shared games corpus; the
        // monthly cache then lives under the lab directory, which it does own.
        try
        {
            Directory.CreateDirectory(shared);
            return shared;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return fallback;
        }
    }

    internal static bool TryParseArchiveMonth(string archiveUrl, out int year, out int month)
    {
        year = 0;
        month = 0;
        if (!Uri.TryCreate(archiveUrl, UriKind.Absolute, out var uri)) return false;
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        return int.TryParse(parts[^2], out year) && year is >= 1990 and <= 9999
            && int.TryParse(parts[^1], out month) && month is >= 1 and <= 12;
    }

    internal static bool IsFinishedMonth(int year, int month, DateTime utcNow)
        => year < utcNow.Year || (year == utcNow.Year && month < utcNow.Month);

    internal static HttpRequestMessage ChessComRequest(string url, string? ifNoneMatch)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!req.Headers.UserAgent.TryParseAdd(ContactUserAgent))
            req.Headers.TryAddWithoutValidation("User-Agent", ContactUserAgent);
        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
            req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return req;
    }

    public static string DefaultOut(string user, string site)
    {
        try
        {
            return Path.Combine(LaplaceInstall.ResolveChessGamesDir(), $"{Sanitize(user)}_{site}.pgn");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException)
        {
            return Path.Combine(ChessLabPaths.LabDir, $"{Sanitize(user)}_{site}.pgn");
        }
    }

    public static Task<int> FetchAsync(
        string user, string site, int? max, int minTcSeconds, string outPath, Action<string>? log,
        CancellationToken ct,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? onBatchReady = null,
        int? concurrency = null)
        => NormalizeSite(site) switch
        {
            "lichess" => FetchLichessAsync(user, max, outPath, log, ct),
            "chesscom" or "chess.com" or "chess" => FetchChessComAsync(
                user, max, minTcSeconds, outPath, log, ct, onBatchReady, concurrency),
            _ => throw new ArgumentException($"unknown site '{site}' (chesscom|lichess)", nameof(site)),
        };

    public static Task<ChessPlayerProfile> FetchProfileAsync(
        string identifier, string site, CancellationToken ct)
        => NormalizeSite(site) switch
        {
            "lichess" => FetchLichessProfileAsync(identifier, ct),
            "chesscom" or "chess.com" or "chess" => FetchChessComProfileAsync(identifier, ct),
            "fide" => FetchFideProfileAsync(identifier, ct),
            _ => throw new ArgumentException($"unknown player profile site '{site}' (lichess|chesscom|fide)", nameof(site)),
        };

    public static async Task<int> FetchChessComAsync(
        string user, int? max, int minTcSeconds, string outPath, Action<string>? log,
        CancellationToken ct,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? onBatchReady = null,
        int? concurrency = null)
    {
        ValidateLimit(max);
        var archUrl = ChessComArchivesUrl(user);
        log?.Invoke($"chess.com archives: {archUrl}");
        // Route the archive-index request through the same visible provider retry
        // channel as monthly PGNs. A throttle on the very first request must not look
        // like a hung import while later requests report their backoff.
        IReadOnlyList<string> archives;
        string? archJson = await GetOptionalStringAsync(archUrl, ct, log);
        if (archJson is not null)
        {
            using var doc = JsonDocument.Parse(archJson);
            archives = ChronologicalArchiveUrls(
                doc.RootElement.GetProperty("archives").EnumerateArray()
                    .Select(e => e.GetString()!));
        }
        else
        {
            // Chess.com answers 404 ("Data provider not found") on /games/archives for some
            // existing accounts while every monthly archive stays public (an empty month is
            // 200 with an empty body). Enumerate the months from the profile's join date.
            string profileJson = await GetStringWithRetryAsync(ChessComPlayerUrl(user), ct, log: log);
            using var profile = JsonDocument.Parse(profileJson);
            if (!profile.RootElement.TryGetProperty("joined", out var joinedValue)
                || !joinedValue.TryGetInt64(out long joined))
                throw new HttpRequestException(
                    $"chess.com has no archive index for '{user}' and its profile has no join date: {archUrl}",
                    null, HttpStatusCode.NotFound);
            archives = ChessComMonthUrls(user, DateTimeOffset.FromUnixTimeSeconds(joined).UtcDateTime, DateTime.UtcNow);
            log?.Invoke($"  archive index 404 on chess.com; enumerating {archives.Count} months since join");
        }
        // Chess.com's public API is one request at a time. A caller-supplied
        // concurrency does not widen this download.
        _ = concurrency;
        log?.Invoke($"  {archives.Count} monthly archives (serial; oldest-first)"
            + (minTcSeconds > 0 ? $", min base TC {minTcSeconds}s" : ""));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        string cacheDirectory = ArchiveDirectory(user);
        int kept = 0;
        await using (var w = new StreamWriter(outPath, append: false, new UTF8Encoding(false)))
        {
            foreach (string archiveUrl in archives)
            {
                ct.ThrowIfCancellationRequested();
                string pgn = await ReadCachedMonthAsync(
                    cacheDirectory, archiveUrl, DateTime.UtcNow,
                    (url, etag, token) => GetResponseWithRetryAsync(
                        url, token, retryNotFound: true, log: log, ifNoneMatch: etag),
                    ct);
                if (string.IsNullOrWhiteSpace(pgn)) continue;

                var ready = new List<string>();
                foreach (var game in ChronologicalGames(SplitGames(pgn)))
                {
                    if (minTcSeconds > 0 && BaseTcSeconds(game) < minTcSeconds) continue;
                    await w.WriteAsync(game);
                    await w.WriteAsync("\n\n");
                    ready.Add(game);
                    if (++kept >= (max ?? int.MaxValue)) break;
                }

                await w.FlushAsync(ct);
                if (ready.Count > 0 && onBatchReady is not null)
                {
                    log?.Invoke($"  {archiveUrl[^7..]}: validating {ready.Count} downloaded games");
                    try { await onBatchReady(archiveUrl[^7..], ready, ct); }
                    catch (InvalidDataException ex)
                    {
                        throw new InvalidDataException($"{archiveUrl}: {ex.Message}", ex);
                    }
                }
                log?.Invoke($"  {archiveUrl[^7..]}: {ready.Count} processed · {kept} total");
                if (max is { } m && kept >= m) break;
            }
        }
        return kept;
    }

    internal delegate Task<ProviderResponse> ArchiveFetch(string url, string? ifNoneMatch, CancellationToken ct);

    /// <summary>
    /// One month at a time. A finished month with a PGN file is not requested.
    /// The current month sends the stored ETag as <c>If-None-Match</c>.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> LoadMonthsAsync(
        string cacheDirectory, IReadOnlyList<string> archiveUrls, DateTime utcNow,
        ArchiveFetch fetch, CancellationToken ct)
    {
        var bodies = new List<string>(archiveUrls.Count);
        foreach (string archiveUrl in archiveUrls)
        {
            ct.ThrowIfCancellationRequested();
            bodies.Add(await ReadCachedMonthAsync(cacheDirectory, archiveUrl, utcNow, fetch, ct));
        }
        return bodies;
    }

    private static async Task<string> ReadCachedMonthAsync(
        string cacheDirectory, string archiveUrl, DateTime utcNow, ArchiveFetch fetch, CancellationToken ct)
    {
        if (!TryParseArchiveMonth(archiveUrl, out int year, out int month))
        {
            var uncached = await fetch($"{archiveUrl}/pgn", null, ct);
            return uncached.NotModified ? "" : uncached.Body;
        }

        Directory.CreateDirectory(cacheDirectory);
        string stem = Path.Combine(cacheDirectory, $"{year:0000}-{month:00}");
        string pgnPath = stem + ".pgn";
        string etagPath = stem + ".etag";
        if (IsFinishedMonth(year, month, utcNow) && File.Exists(pgnPath))
            return await File.ReadAllTextAsync(pgnPath, ct);

        string? etag = null;
        if (!IsFinishedMonth(year, month, utcNow) && File.Exists(etagPath))
            etag = (await File.ReadAllTextAsync(etagPath, ct)).Trim();

        string pgnUrl = archiveUrl.EndsWith("/pgn", StringComparison.Ordinal) ? archiveUrl : $"{archiveUrl}/pgn";
        var response = await fetch(pgnUrl, etag, ct);
        if (response.NotModified)
        {
            if (File.Exists(pgnPath))
                return await File.ReadAllTextAsync(pgnPath, ct);
            response = await fetch(pgnUrl, null, ct);
        }

        var utf8 = new UTF8Encoding(false);
        await File.WriteAllTextAsync(pgnPath, response.Body, utf8, ct);
        if (!string.IsNullOrWhiteSpace(response.ETag))
            await File.WriteAllTextAsync(etagPath, response.ETag.Trim(), utf8, ct);
        return response.Body;
    }

    internal static IReadOnlyList<string> ChessComMonthUrls(string user, DateTime joinedUtc, DateTime utcNow)
    {
        var months = new List<string>();
        string player = ChessComPlayerUrl(user);
        for (var m = new DateTime(joinedUtc.Year, joinedUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
             m.Year < utcNow.Year || (m.Year == utcNow.Year && m.Month <= utcNow.Month);
             m = m.AddMonths(1))
            months.Add($"{player}/games/{m.Year:0000}/{m.Month:00}");
        return months;
    }

    internal static IReadOnlyList<string> ChronologicalArchiveUrls(IEnumerable<string> archives)
        => archives.Where(static a => !string.IsNullOrWhiteSpace(a))
            .OrderBy(static a => a, StringComparer.Ordinal)
            .ToArray();

    internal static IReadOnlyList<string> ChronologicalGames(IEnumerable<string> games)
        => games.OrderBy(GameDateKey, StringComparer.Ordinal)
            .ThenBy(GameTimeKey, StringComparer.Ordinal)
            .ThenBy(static game => PgnGames.TagStr(game, "Site"), StringComparer.Ordinal)
            .ThenBy(static game => PgnGames.TagStr(game, "White"), StringComparer.Ordinal)
            .ThenBy(static game => PgnGames.TagStr(game, "Black"), StringComparer.Ordinal)
            .ThenBy(static game => PgnGames.TagStr(game, "Result"), StringComparer.Ordinal)
            .ThenBy(static game => game, StringComparer.Ordinal)
            .ToArray();

    private static string GameDateKey(string game)
    {
        string? value = PgnGames.TagStr(game, "UTCDate");
        if (string.IsNullOrWhiteSpace(value)) value = PgnGames.TagStr(game, "Date");
        if (string.IsNullOrWhiteSpace(value)) return "9999.99.99";
        string[] parts = value.Split('.');
        if (parts.Length != 3) return "9999.99.99";
        return $"{ChronologyPart(parts[0], 4, "9999")}.{ChronologyPart(parts[1], 2, "99")}.{ChronologyPart(parts[2], 2, "99")}";
    }

    private static string GameTimeKey(string game)
    {
        string? value = PgnGames.TagStr(game, "UTCTime");
        if (string.IsNullOrWhiteSpace(value)) value = PgnGames.TagStr(game, "StartTime");
        if (string.IsNullOrWhiteSpace(value)) return "99:99:99";
        string[] parts = value.Split(':');
        if (parts.Length < 2) return "99:99:99";
        string sec = parts.Length > 2 ? ChronologyPart(parts[2], 2, "99") : "99";
        return $"{ChronologyPart(parts[0], 2, "99")}:{ChronologyPart(parts[1], 2, "99")}:{sec}";
    }

    private static string ChronologyPart(string value, int width, string unknown)
    {
        value = value.Trim();
        return value.Length > 0 && value.All(char.IsDigit)
            ? value.PadLeft(width, '0')
            : unknown;
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
        ValidateLimit(max);
        var url = LichessGamesUrl(user, max);
        log?.Invoke($"lichess: {url}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        for (int attempt = 0; ; attempt++)
        {
            TimeSpan? retryDelay = null;
            await LichessRequests.WaitAsync(ct);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/x-chess-pgn");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (IsTransientProviderStatus(resp.StatusCode, retryNotFound: false)
                    && attempt + 1 < ProviderMaxAttempts)
                {
                    retryDelay = ProviderRetryDelay(resp, attempt);
                    log?.Invoke($"  lichess HTTP {(int)resp.StatusCode}; retrying in {retryDelay.Value.TotalSeconds:F1}s");
                }
                else
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(outPath);
                    await src.CopyToAsync(dst, ct);
                    break;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt + 1 < ProviderMaxAttempts)
            {
                retryDelay = ProviderBackoff(attempt);
                log?.Invoke($"  lichess transport failure; retrying in {retryDelay.Value.TotalSeconds:F1}s");
            }
            catch (IOException) when (attempt + 1 < ProviderMaxAttempts)
            {
                retryDelay = ProviderBackoff(attempt);
                log?.Invoke($"  lichess stream failure; retrying in {retryDelay.Value.TotalSeconds:F1}s");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt + 1 < ProviderMaxAttempts)
            {
                retryDelay = ProviderBackoff(attempt);
                log?.Invoke($"  lichess request timeout; retrying in {retryDelay.Value.TotalSeconds:F1}s");
            }
            finally
            {
                LichessRequests.Release();
            }

            if (retryDelay is null)
                throw new HttpRequestException($"lichess request failed without a retryable response: {url}");
            await Task.Delay(retryDelay.Value, ct);
        }

        // sort=dateAsc makes the streamed provider order itself the chronology contract.
        return PgnGames.StreamGames(outPath).Count();
    }

    public static async Task<ChessPlayerProfile> FetchLichessProfileAsync(string user, CancellationToken ct)
    {
        string json = await GetStringWithRetryAsync(
            $"https://lichess.org/api/user/{Uri.EscapeDataString(user)}", ct);
        return ParseLichessProfile(json, user);
    }

    internal static ChessPlayerProfile ParseLichessProfile(string json, string requestedUser)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string username = String(root, "username") ?? String(root, "id") ?? requestedUser;
        JsonElement profile = root.TryGetProperty("profile", out var p) ? p : default;
        string? firstName = profile.ValueKind == JsonValueKind.Object ? String(profile, "firstName") : null;
        string? lastName = profile.ValueKind == JsonValueKind.Object ? String(profile, "lastName") : null;
        string? realName = profile.ValueKind == JsonValueKind.Object ? String(profile, "realName") : null;
        if (string.IsNullOrWhiteSpace(realName))
            realName = string.Join(' ', new[] { firstName, lastName }
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!));
        if (string.IsNullOrWhiteSpace(realName)) realName = null;
        var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("perfs", out var perfs))
            foreach (string key in new[] { "standard", "classical", "rapid", "blitz", "bullet" })
                if (perfs.TryGetProperty(key, out var perf) && perf.TryGetProperty("rating", out var rating)
                    && rating.TryGetInt32(out int value))
                    ratings[key] = value;
        if (profile.ValueKind == JsonValueKind.Object)
            foreach (var (property, label) in new[]
            {
                ("fideRating", "fide"), ("uscfRating", "uscf"), ("ecfRating", "ecf"),
            })
                if (profile.TryGetProperty(property, out var rating) && rating.TryGetInt32(out int value))
                    ratings[label] = value;

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
            realName,
            profile.ValueKind == JsonValueKind.Object ? String(profile, "bio") : null,
            String(root, "title"),
            profile.ValueKind == JsonValueKind.Object ? String(profile, "country") : null,
            null,
            null,
            PlayerAliases(username, realName),
            links.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), ratings,
            Facts(root, "createdAt", "seenAt", "playTime", "disabled", "tosViolation"));
    }

    public static async Task<ChessPlayerProfile> FetchChessComProfileAsync(string user, CancellationToken ct)
    {
        string json = await GetStringWithRetryAsync(ChessComPlayerUrl(user), ct);
        // Ratings are enrichment: chess.com 404s /stats for some existing accounts.
        string statsJson = await GetOptionalStringAsync(ChessComStatsUrl(user), ct) ?? "{}";
        return ParseChessComProfile(json, statsJson, user);
    }

    internal static ChessPlayerProfile ParseChessComProfile(
        string json, string statsJson, string requestedUser)
    {
        using var doc = JsonDocument.Parse(json);
        using var statsDoc = JsonDocument.Parse(statsJson);
        var root = doc.RootElement;
        string username = String(root, "username") ?? requestedUser;
        string? realName = String(root, "name");
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
        string? profileUrl = String(root, "url");
        if (profileUrl is not null) links.Add(profileUrl);
        if (String(root, "country") is { } country) links.Add(country);
        if (String(root, "twitch_url") is { } twitch) links.Add(twitch);
        if (root.TryGetProperty("streaming_platforms", out var platforms)
            && platforms.ValueKind == JsonValueKind.Array)
            foreach (var platform in platforms.EnumerateArray())
                if (String(platform, "url") is { } streamUrl) links.Add(streamUrl);
        string displayName = ChessComMemberHandle(profileUrl) ?? username;
        return new ChessPlayerProfile(
            "chesscom", username, displayName, realName, null,
            String(root, "title"), String(root, "country"), null,
            String(root, "avatar"), PlayerAliases(username, displayName, realName),
            links.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), ratings,
            Facts(root, "player_id", "@id", "status", "location", "joined", "last_online",
                "followers", "is_streamer", "twitch_url", "league", "verified"));
    }

    internal static string? ChessComMemberHandle(string? profileUrl)
    {
        if (string.IsNullOrWhiteSpace(profileUrl)
            || !Uri.TryCreate(profileUrl, UriKind.Absolute, out var uri))
            return null;
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[^2].Equals("member", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(parts[^1])
            : null;
    }
}
