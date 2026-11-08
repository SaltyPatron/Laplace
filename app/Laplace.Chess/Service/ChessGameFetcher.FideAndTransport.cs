using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

public static partial class ChessGameFetcher
{
    public static async Task<ChessPlayerProfile> FetchFideProfileAsync(string fideId, CancellationToken ct)
    {
        if (!fideId.All(char.IsDigit) || fideId.Length is < 4 or > 12)
            throw new ArgumentException("FIDE identifier must contain 4 to 12 digits.", nameof(fideId));
        string url = $"https://ratings.fide.com/profile/{fideId}";
        string html = await GetStringWithRetryAsync(url, ct);
        if (!FideProfileContainsPlayerName(html))
        {
            string freshHtml = await GetStringWithRetryAsync($"{url}?_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", ct);
            if (FideProfileContainsPlayerName(freshHtml))
                html = freshHtml;
        }
        return ParseFideProfile(html, fideId, url);
    }

    internal static bool FideProfileContainsPlayerName(string html)
        => !string.IsNullOrWhiteSpace(html)
           && (HtmlMatch(html, @"<title[^>]*>\s*\uFEFF?\s*(.*?)\s+FIDE Profile\s*</title>") is not null
               || HtmlMatch(html, @"<h1[^>]*>\s*\uFEFF?\s*(.*?)\s*</h1>") is not null);

    internal static ChessPlayerProfile ParseFideProfile(string html, string fideId, string url)
    {
        string text = WebText(html);
        string name = HtmlMatch(html, @"<title[^>]*>\s*\uFEFF?\s*(.*?)\s+FIDE Profile\s*</title>")
            ?? HtmlMatch(html, @"<h1[^>]*>\s*\uFEFF?\s*(.*?)\s*</h1>")
            ?? throw new InvalidDataException("FIDE profile did not contain a player name.");
        string? pageFideId = Match(text, @"FIDE ID\s+(\d{4,12})\b");
        if (pageFideId is not null && !pageFideId.Equals(fideId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"FIDE profile id mismatch: requested {fideId}, provider returned {pageFideId}.");
        var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        AddRating(text, ratings, "standard", @"(\d{3,4})\s+STANDARD\b");
        AddRating(text, ratings, "rapid", @"(\d{3,4})\s+RAPID\b");
        AddRating(text, ratings, "blitz", @"(\d{3,4})\s+BLITZ\b");
        string? avatar = HtmlAttribute(html,
            @"<meta[^>]+property\s*=\s*['""]og:image['""][^>]+content\s*=\s*['""]([^'""]+)")
            ?? HtmlAttribute(html,
                @"<img[^>]+(?:class\s*=\s*['""][^'""]*(?:profile|player)[^'""]*['""])[^>]+src\s*=\s*['""]([^'""]+)");
        return new ChessPlayerProfile(
            "fide", fideId, name, name, null,
            Match(text, @"FIDE title\s+(.+?)\s+(?:World Rank|Titles|Period)\b"),
            Match(text, @"FIDE ID\s+\d+\s+Federation\s+(.+?)\s+B-Year\b")
                ?? Match(text, @"(?<!Chess )Federation\s+(.+?)\s+(?:B-Year|Gender|FIDE title)\b"),
            fideId, avatar, PlayerAliases(name), [url], ratings,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["birth_year"] = Match(text, @"B-Year\s+(\d{4})\b") ?? "",
                ["gender"] = Match(text, @"Gender\s+(.+?)\s+FIDE title\b") ?? "",
                ["world_rank"] = Match(text, @"World Rank\s+(\d+)\b") ?? "",
            }.Where(static x => x.Value.Length > 0)
             .ToDictionary(static x => x.Key, static x => x.Value, StringComparer.OrdinalIgnoreCase));
    }

    public static async Task<IReadOnlyList<FidePlayerCandidate>> SearchFideAsync(
        string query, int limit, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2) throw new ArgumentException("FIDE search needs at least two characters.", nameof(query));
        if (query.Length is >= 4 and <= 12 && query.All(char.IsDigit))
        {
            var profile = await FetchFideProfileAsync(query, ct);
            int birthYear = profile.Facts.TryGetValue("birth_year", out var born)
                && int.TryParse(born, out int year) ? year : 0;
            return [new FidePlayerCandidate(
                profile.ProviderId,
                profile.DisplayName,
                profile.Title,
                profile.Federation ?? "",
                profile.Ratings.TryGetValue("standard", out int standard) ? standard : 0,
                profile.Ratings.TryGetValue("rapid", out int rapid) ? rapid : 0,
                profile.Ratings.TryGetValue("blitz", out int blitz) ? blitz : 0,
                birthYear,
                null)];
        }
        var candidates = new Dictionary<string, FidePlayerCandidate>(StringComparer.Ordinal);
        foreach (string term in FideSearchTerms(query))
        {
            string url = "https://ratings.fide.com/incl_search_l.php?search="
                + Uri.EscapeDataString(term) + "&simple=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://ratings.fide.com/");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            foreach (var candidate in ParseFideSearch(await SendStringWithRetryAsync(req, ct)))
                candidates[candidate.FideId] = candidate;
        }
        return candidates.Values
            .OrderBy(candidate => FideCandidateScore(query, candidate.Name))
            .ThenByDescending(static candidate => candidate.Standard)
            .Take(Math.Clamp(limit, 1, 100)).ToArray();
    }

    public static async Task<IReadOnlyList<FidePlayerCandidate>> FetchFideTopAsync(
        string cohort, int limit, CancellationToken ct)
    {
        cohort = cohort.Trim().ToLowerInvariant();
        if (!FideCohorts.Contains(cohort))
            throw new ArgumentException($"unknown FIDE cohort '{cohort}'", nameof(cohort));
        string html = await GetStringWithRetryAsync(
            $"https://ratings.fide.com/a_top.php?list={Uri.EscapeDataString(cohort)}", ct);
        return ParseFideTop(html, cohort).Take(Math.Clamp(limit, 1, 100)).ToArray();
    }

    public static readonly IReadOnlySet<string> FideCohorts = new HashSet<string>(StringComparer.Ordinal)
    {
        "open", "women", "juniors", "girls",
        "men_rapid", "women_rapid", "juniors_rapid", "girls_rapid",
        "men_blitz", "women_blitz", "juniors_blitz", "girls_blitz",
    };

    internal static IReadOnlyList<FidePlayerCandidate> ParseFideSearch(string html)
    {
        var result = new List<FidePlayerCandidate>();
        foreach (Match row in Regex.Matches(html, @"<tr[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string body = row.Groups[1].Value;
            var profile = Regex.Match(body,
                @"href\s*=\s*['""][^'""]*profile/(\d{4,12})[^'""]*['""][^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!profile.Success) continue;
            var ratings = Regex.Matches(body, @"data-label\s*=\s*['""]Rtg['""][^>]*>(.*?)</td>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Select(static m => IntText(m.Groups[1].Value)).ToArray();
            result.Add(new FidePlayerCandidate(
                profile.Groups[1].Value,
                CleanHtml(profile.Groups[2].Value),
                CleanHtml(HtmlCell(body, "title") ?? ""),
                Regex.Match(body, @"<img[^>]+alt=['""]([A-Z]{3})['""]", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant(),
                ratings.ElementAtOrDefault(0), ratings.ElementAtOrDefault(1), ratings.ElementAtOrDefault(2),
                IntText(HtmlCell(body, "B-Year") ?? ""), null));
        }
        return result;
    }

    internal static IReadOnlyList<FidePlayerCandidate> ParseFideTop(string html, string cohort)
    {
        var result = new List<FidePlayerCandidate>();
        foreach (Match row in Regex.Matches(html, @"<tr[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string body = row.Groups[1].Value;
            var profile = Regex.Match(body,
                @"href\s*=\s*['""][^'""]*profile/(\d{4,12})[^'""]*['""][^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!profile.Success) continue;
            int rating = IntText(Regex.Match(body, @"class\s*=\s*['""]?rating_column['""]?[^>]*>(.*?)</td>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value);
            if (rating == 0)
                rating = IntText(HtmlCell(body, "Rating") ?? HtmlCell(body, "Rtg") ?? "");
            int rank = IntText(Regex.Match(body, @"class\s*=\s*['""]rank_span['""][^>]*>(.*?)</span>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value);
            if (rank == 0)
                rank = IntText(Regex.Match(body, @"<td[^>]*>\s*(\d{1,3})\s*</td>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value);
            int birthYear = IntText(Regex.Match(body, @"class\s*=\s*['""]?bday_column['""]?[^>]*>(.*?)</td>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value);
            if (birthYear == 0)
                birthYear = IntText(HtmlCell(body, "B-Year") ?? "");
            var mode = cohort.EndsWith("_rapid", StringComparison.Ordinal) ? "rapid"
                : cohort.EndsWith("_blitz", StringComparison.Ordinal) ? "blitz" : "standard";
            result.Add(new FidePlayerCandidate(
                profile.Groups[1].Value, CleanHtml(profile.Groups[2].Value), null,
                FideFederation(body),
                mode == "standard" ? rating : 0,
                mode == "rapid" ? rating : 0,
                mode == "blitz" ? rating : 0,
                birthYear, rank));
        }
        return result;
    }

    internal static IReadOnlyList<string> FideSearchTerms(string query)
    {
        query = Regex.Replace(query.Trim(), @"\s+", " ");
        var terms = new List<string> { query };
        string canonical = PlayerAlias.Canonical(query);
        if (!canonical.Equals(query, StringComparison.OrdinalIgnoreCase)) terms.Add(canonical);
        string[] names = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length >= 2)
        {
            string surnameFirst = $"{names[^1]}, {string.Join(' ', names[..^1])}";
            if (!terms.Contains(surnameFirst, StringComparer.OrdinalIgnoreCase)) terms.Add(surnameFirst);
        }
        return terms;
    }

    internal static int? ResolveArchiveLimit(bool all, string? configuredLimit)
    {
        if (all) return null;
        if (!int.TryParse(configuredLimit, out int limit) || limit <= 0)
            throw new ArgumentException("A positive game limit is required when Ingest all games is off.",
                nameof(configuredLimit));
        return limit;
    }

    internal static string ChessComPlayerUrl(string user)
        => $"https://api.chess.com/pub/player/{Uri.EscapeDataString(ChessComCanonicalUser(user))}";

    internal static string ChessComStatsUrl(string user)
        => $"{ChessComPlayerUrl(user)}/stats";

    internal static string ChessComArchivesUrl(string user)
        => $"{ChessComPlayerUrl(user)}/games/archives";

    internal static string ChessComCanonicalUser(string user)
        => user.Trim().ToLowerInvariant();

    internal static string LichessGamesUrl(string user, int? max)
    {
        ValidateLimit(max);
        string url = $"https://lichess.org/api/games/user/{Uri.EscapeDataString(user)}?sort=dateAsc";
        return max is { } limit ? $"{url}&max={limit}" : url;
    }

    internal static int FideCandidateScore(string query, string candidate)
    {
        string q = PlayerAlias.Canonical(query);
        string name = PlayerAlias.Canonical(candidate);
        if (q == name) return 0;
        if (q.Replace(" ", "", StringComparison.Ordinal)
            == name.Replace(" ", "", StringComparison.Ordinal)) return 1;
        string[] tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] nameTokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.All(token => nameTokens.Contains(token, StringComparer.Ordinal))) return 2;
        if (name.Contains(q, StringComparison.Ordinal)) return 3;
        return 10;
    }

    private static string FideFederation(string row)
    {
        var alt = Regex.Match(row, @"<img[^>]+alt=['""]([A-Z]{3})['""]", RegexOptions.IgnoreCase);
        if (alt.Success) return alt.Groups[1].Value.ToUpperInvariant();
        var text = Regex.Match(row,
            @"class\s*=\s*['""]?flag-wrapper['""]?[^>]*>.*?<img[^>]*>\s*([A-Z]{3})\b",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (text.Success) return text.Groups[1].Value.ToUpperInvariant();
        var generic = Regex.Match(CleanHtml(row), @"\b([A-Z]{3})\b");
        return generic.Success ? generic.Groups[1].Value.ToUpperInvariant() : "";
    }

    private static IReadOnlyDictionary<string, string> Facts(JsonElement root, params string[] names)
    {
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;
            string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
            if (text.Length > 0) facts[name] = text;
        }
        return facts;
    }

    private static string? HtmlCell(string row, string label)
    {
        var match = Regex.Match(row,
            @"data-label\s*=\s*['""]" + Regex.Escape(label) + @"['""][^>]*>(.*?)</td>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int IntText(string html)
        => int.TryParse(Regex.Match(CleanHtml(html), @"\d+").Value, out int value) ? value : 0;

    private static string CleanHtml(string html)
        => System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "))
            .Replace('\u00a0', ' ').Trim();

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

    private static string? HtmlAttribute(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    private static IReadOnlyList<string> PlayerAliases(params string?[] values)
    {
        var aliases = values.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim()).ToList();
        foreach (string value in aliases.ToArray())
        {
            string canonical = PlayerAlias.Canonical(value);
            if (canonical.Length > 0) aliases.Add(canonical);
        }
        return aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddRating(string text, Dictionary<string, int> ratings, string key, string pattern)
    {
        if (int.TryParse(Match(text, pattern), out int value)) ratings[key] = value;
    }

    private static string NormalizeSite(string site) => site.Trim().ToLowerInvariant();

    private static void ValidateLimit(int? max)
    {
        if (max is <= 0) throw new ArgumentOutOfRangeException(nameof(max), "Game limit must be positive.");
    }

    private static async Task<string> GetStringWithRetryAsync(
        string url, CancellationToken ct, bool retryNotFound = false, Action<string>? log = null)
    {
        var response = await GetResponseWithRetryAsync(url, ct, retryNotFound, log, ifNoneMatch: null);
        if (response.NotModified)
            throw new HttpRequestException($"provider returned 304 without a conditional request: {url}");
        return response.Body;
    }

    private static async Task<ProviderResponse> GetResponseWithRetryAsync(
        string url, CancellationToken ct, bool retryNotFound = false, Action<string>? log = null,
        string? ifNoneMatch = null)
    {
        SemaphoreSlim? gate = ProviderGate(url);
        for (int attempt = 0; ; attempt++)
        {
            TimeSpan? retryDelay = null;
            if (gate is not null) await gate.WaitAsync(ct);
            try
            {
                using var req = ChessComRequest(url, ifNoneMatch);
                using var resp = await Http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.NotModified)
                    return new ProviderResponse("", resp.Headers.ETag?.Tag, NotModified: true);
                if (IsTransientProviderStatus(resp.StatusCode, retryNotFound)
                    && attempt + 1 < ProviderMaxAttempts)
                {
                    retryDelay = ProviderRetryDelay(resp, attempt);
                    log?.Invoke($"  provider HTTP {(int)resp.StatusCode}; retrying in {retryDelay.Value.TotalSeconds:F1}s");
                }
                else
                {
                    resp.EnsureSuccessStatusCode();
                    return new ProviderResponse(
                        await resp.Content.ReadAsStringAsync(ct), resp.Headers.ETag?.Tag, NotModified: false);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt + 1 < ProviderMaxAttempts)
            {
                retryDelay = ProviderBackoff(attempt);
                log?.Invoke($"  provider transport failure; retrying in {retryDelay.Value.TotalSeconds:F1}s");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt + 1 < ProviderMaxAttempts)
            {
                retryDelay = ProviderBackoff(attempt);
                log?.Invoke($"  provider request timeout; retrying in {retryDelay.Value.TotalSeconds:F1}s");
            }
            finally
            {
                gate?.Release();
            }

            if (retryDelay is null)
                throw new HttpRequestException($"provider request failed without a retryable response: {url}");
            await Task.Delay(retryDelay.Value, ct);
        }
    }

    private static SemaphoreSlim? ProviderGate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.Equals("api.chess.com", StringComparison.OrdinalIgnoreCase))
            return ChessComRequests;
        if (uri.Host.Equals("lichess.org", StringComparison.OrdinalIgnoreCase))
            return LichessRequests;
        return null;
    }

    private static bool IsTransientProviderStatus(HttpStatusCode status, bool retryNotFound)
        => status == HttpStatusCode.TooManyRequests
           || status == HttpStatusCode.RequestTimeout
           || (int)status >= 500
           || (retryNotFound && status == HttpStatusCode.NotFound);

    private static TimeSpan ProviderRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta && delta > TimeSpan.Zero)
            return LimitProviderDelay(delta);
        if (retry?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return LimitProviderDelay(wait);
        }
        // Lichess explicitly requires a full minute after HTTP 429, and Chess.com
        // warns that parallel/rate-limited access can produce the same status.
        // Retry-After wins when present; otherwise use the conservative provider floor.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return TimeSpan.FromMinutes(1);
        return ProviderBackoff(attempt);
    }

    private static TimeSpan ProviderBackoff(int attempt)
        => TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * (1 << Math.Min(attempt, 5))));

    private static TimeSpan LimitProviderDelay(TimeSpan delay)
        => delay > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : delay;

    private static async Task<string> SendStringWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var resp = await Http.SendAsync(copy, ct);
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
    string? AvatarUrl,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Links,
    IReadOnlyDictionary<string, int> Ratings,
    IReadOnlyDictionary<string, string> Facts);

public sealed record FidePlayerCandidate(
    string FideId,
    string Name,
    string? Title,
    string Federation,
    int Standard,
    int Rapid,
    int Blitz,
    int BirthYear,
    int? Rank);
