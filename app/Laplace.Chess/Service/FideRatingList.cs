using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// Reads FIDE's first-party combined rating artifact. Search and roster discovery
/// belong to the published player estate; profile HTML is enrichment for a selected
/// identity, not the authority for discovering whether the identity exists.
///
/// FIDE publishes XML, and XML is already a registered Laplace grammar. The native
/// grammar decomposer therefore owns structural parsing; this class only projects
/// FIDE's named fields from the resulting element AST.
/// </summary>
internal static class FideRatingList
{
    internal const string XmlArchiveUrl = "https://ratings.fide.com/download/players_list_xml.zip";
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private const long MaxXmlBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim EstateGate = new(1, 1);
    private static readonly HttpClient Http = CreateClient();

    private static Player[]? _players;
    private static DateTimeOffset _playersFetchedAt;

    internal sealed record Player(
        string FideId,
        string Name,
        string Federation,
        string Sex,
        string Title,
        int Standard,
        int Rapid,
        int Blitz,
        int BirthYear,
        string Flag);

    private sealed class PlayerBuilder
    {
        public string FideId = "";
        public string Name = "";
        public string Federation = "";
        public string Sex = "";
        public string Title = "";
        public int Standard;
        public int Rapid;
        public int Blitz;
        public string Birthday = "";
        public string Flag = "";
    }

    private readonly record struct ElementFrame(uint EndByte, string Name, PlayerBuilder? Player);

    public static async Task<IReadOnlyList<FidePlayerCandidate>> SearchAsync(
        string query, int limit, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2)
            throw new ArgumentException("FIDE search needs at least two characters.", nameof(query));

        string canonical = PlayerAlias.Canonical(query);
        int cap = Math.Clamp(limit, 1, 100);
        var players = await GetPlayersAsync(ct).ConfigureAwait(false);
        return await Task.Run(
            () => SearchPlayers(players, canonical, cap, ct),
            ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<FidePlayerCandidate>> TopAsync(
        string cohort, int limit, CancellationToken ct)
    {
        cohort = cohort.Trim().ToLowerInvariant();
        if (!ChessGameFetcher.FideCohorts.Contains(cohort))
            throw new ArgumentException($"unknown FIDE cohort '{cohort}'", nameof(cohort));

        var players = await GetPlayersAsync(ct).ConfigureAwait(false);
        return await Task.Run(
            () => TopPlayers(players, cohort, limit, DateTime.UtcNow.Year, ct),
            ct).ConfigureAwait(false);
    }

    internal static IReadOnlyList<FidePlayerCandidate> SearchXml(
        Stream xml, string query, int limit, CancellationToken ct = default)
    {
        byte[] bytes = ReadBounded(xml, MaxXmlBytes, "FIDE XML player list", ct);
        var players = ParsePlayers(bytes, ct);
        return SearchPlayers(
            players,
            PlayerAlias.Canonical(query.Trim()),
            Math.Clamp(limit, 1, 100),
            ct);
    }

    internal static IReadOnlyList<FidePlayerCandidate> TopXml(
        Stream xml, string cohort, int limit, int currentYear, CancellationToken ct = default)
    {
        byte[] bytes = ReadBounded(xml, MaxXmlBytes, "FIDE XML player list", ct);
        var players = ParsePlayers(bytes, ct);
        return TopPlayers(players, cohort.Trim().ToLowerInvariant(), limit, currentYear, ct);
    }

    internal static IReadOnlyList<FidePlayerCandidate> SearchArchive(
        byte[] archive, string query, int limit, CancellationToken ct = default)
    {
        var players = ParsePlayers(ExtractXml(archive, ct), ct);
        return SearchPlayers(
            players,
            PlayerAlias.Canonical(query.Trim()),
            Math.Clamp(limit, 1, 100),
            ct);
    }

    internal static IReadOnlyList<FidePlayerCandidate> TopArchive(
        byte[] archive, string cohort, int limit, int currentYear, CancellationToken ct = default)
    {
        var players = ParsePlayers(ExtractXml(archive, ct), ct);
        return TopPlayers(players, cohort.Trim().ToLowerInvariant(), limit, currentYear, ct);
    }

    private static IReadOnlyList<FidePlayerCandidate> SearchPlayers(
        IReadOnlyList<Player> players, string canonical, int cap, CancellationToken ct)
    {
        int seen = 0;
        return players
            .Where(p =>
            {
                if ((++seen & 4095) == 0) ct.ThrowIfCancellationRequested();
                return CandidateScore(canonical, p.Name) < 10;
            })
            .OrderBy(p => CandidateScore(canonical, p.Name))
            .ThenByDescending(p => p.Standard)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.FideId, StringComparer.Ordinal)
            .Take(cap)
            .Select(ToCandidate)
            .ToArray();
    }

    private static IReadOnlyList<FidePlayerCandidate> TopPlayers(
        IReadOnlyList<Player> players, string cohort, int limit, int currentYear, CancellationToken ct)
    {
        int cap = Math.Clamp(limit, 1, 100);
        string mode = Mode(cohort);
        bool women = cohort.StartsWith("women", StringComparison.Ordinal)
            || cohort.StartsWith("girls", StringComparison.Ordinal);
        bool junior = cohort.StartsWith("juniors", StringComparison.Ordinal)
            || cohort.StartsWith("girls", StringComparison.Ordinal);
        int juniorBirthFloor = currentYear - 20;
        int seen = 0;

        return players
            .Where(p =>
            {
                if ((++seen & 4095) == 0) ct.ThrowIfCancellationRequested();
                return !IsInactive(p.Flag);
            })
            .Where(p => !women || p.Sex.Equals("F", StringComparison.OrdinalIgnoreCase))
            .Where(p => !junior || p.BirthYear >= juniorBirthFloor)
            .Select(p => (Player: p, Rating: RatingFor(p, mode)))
            .Where(x => x.Rating > 0)
            .OrderByDescending(x => x.Rating)
            .ThenBy(x => x.Player.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Player.FideId, StringComparer.Ordinal)
            .Take(cap)
            .Select((x, i) => ToCandidate(x.Player) with { Rank = i + 1 })
            .ToArray();
    }

    private static async Task<Player[]> GetPlayersAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cached = _players;
        if (cached is not null && now - _playersFetchedAt < RefreshAfter)
            return cached;

        await EstateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            cached = _players;
            if (cached is not null && now - _playersFetchedAt < RefreshAfter)
                return cached;

            byte[] archive = await DownloadArchiveAsync(ct).ConfigureAwait(false);
            byte[] xml = ExtractXml(archive, ct);
            var players = await Task.Run(() => ParsePlayers(xml, ct), ct).ConfigureAwait(false);
            if (players.Length == 0)
                throw new InvalidDataException(
                    "FIDE published rating artifact parsed successfully but contained no valid player records.");

            _players = players;
            _playersFetchedAt = now;
            return players;
        }
        finally
        {
            EstateGate.Release();
        }
    }

    private static async Task<byte[]> DownloadArchiveAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, XmlArchiveUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream", 0.9));
        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > MaxArchiveBytes)
            throw new InvalidDataException(
                $"FIDE rating archive is larger than the {MaxArchiveBytes} byte safety cap.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(block.AsMemory(0, block.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxArchiveBytes)
                throw new InvalidDataException(
                    $"FIDE rating archive exceeded the {MaxArchiveBytes} byte safety cap while reading.");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private static byte[] ExtractXml(byte[] archive, CancellationToken ct)
    {
        using var backing = new MemoryStream(archive, writable: false);
        using var zip = new ZipArchive(backing, ZipArchiveMode.Read, leaveOpen: false);
        var xmlEntries = zip.Entries
            .Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Name.Contains("players", StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.FullName, StringComparer.Ordinal)
            .ToArray();
        if (xmlEntries.Length == 0)
            throw new InvalidDataException("FIDE rating archive did not contain an XML player list.");

        var entry = xmlEntries[0];
        if (entry.Length <= 0 || entry.Length > MaxXmlBytes)
            throw new InvalidDataException("FIDE XML player list has an invalid expanded size.");

        using var input = entry.Open();
        return ReadBounded(input, MaxXmlBytes, "FIDE XML player list", ct);
    }

    /// <summary>
    /// Structural parsing belongs to the registered XML grammar. This projection only
    /// interprets the direct child elements of each FIDE <player> element after that
    /// structure has been produced by the native grammar decomposer.
    /// </summary>
    private static Player[] ParsePlayers(byte[] utf8, CancellationToken ct)
    {
        using var ast = GrammarDecomposer.Parse(utf8, "xml");
        var players = new List<PlayerBuilder>();
        var stack = new Stack<ElementFrame>();

        for (int i = 0; i < ast.NodeCount; i++)
        {
            if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
            var node = ast.GetNode(i);
            if (!ast.NodeTypeIs(node.NodeTypeId, "element"u8)) continue;

            while (stack.Count > 0 && node.StartByte >= stack.Peek().EndByte)
                stack.Pop();

            string name = ElementName(ast, utf8, i, node);
            PlayerBuilder? player = null;
            if (name.Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                player = new PlayerBuilder();
                players.Add(player);
            }
            else if (stack.Count > 0
                     && stack.Peek().Name.Equals("player", StringComparison.OrdinalIgnoreCase)
                     && stack.Peek().Player is { } owner)
            {
                ApplyField(owner, name, ElementText(ast, utf8, i, node));
            }

            stack.Push(new ElementFrame(node.EndByte, name, player));
        }

        return players
            .Where(p => p.FideId.Length is >= 4 and <= 12
                        && p.FideId.All(char.IsDigit)
                        && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new Player(
                p.FideId,
                p.Name.Trim(),
                p.Federation.Trim().ToUpperInvariant(),
                p.Sex.Trim().ToUpperInvariant(),
                NormalizeTitle(p.Title),
                p.Standard,
                p.Rapid,
                p.Blitz,
                BirthYear(p.Birthday),
                p.Flag.Trim()))
            .ToArray();
    }

    private static string ElementName(
        GrammarAst ast, byte[] utf8, int elementIndex, LaplaceAstNode element)
    {
        int startTagIndex = -1;
        LaplaceAstNode startTag = default;
        for (int i = elementIndex + 1; i < ast.NodeCount; i++)
        {
            var node = ast.GetNode(i);
            if (node.StartByte >= element.EndByte) break;
            if (node.Parent != (uint)elementIndex) continue;
            if (!ast.NodeTypeIs(node.NodeTypeId, "STag"u8)
                && !ast.NodeTypeIs(node.NodeTypeId, "EmptyElemTag"u8))
                continue;
            startTagIndex = i;
            startTag = node;
            break;
        }
        if (startTagIndex < 0) return "";

        for (int i = startTagIndex + 1; i < ast.NodeCount; i++)
        {
            var node = ast.GetNode(i);
            if (node.StartByte >= startTag.EndByte) break;
            if (node.Parent == (uint)startTagIndex && ast.NodeTypeIs(node.NodeTypeId, "Name"u8))
                return DecodeSpan(utf8, node.StartByte, node.EndByte);
        }
        return "";
    }

    private static string ElementText(
        GrammarAst ast, byte[] utf8, int elementIndex, LaplaceAstNode element)
    {
        uint contentStart = element.StartByte;
        uint contentEnd = element.EndByte;
        bool sawStart = false;

        for (int i = elementIndex + 1; i < ast.NodeCount; i++)
        {
            var node = ast.GetNode(i);
            if (node.StartByte >= element.EndByte) break;
            if (node.Parent != (uint)elementIndex) continue;

            if (!sawStart && (ast.NodeTypeIs(node.NodeTypeId, "STag"u8)
                              || ast.NodeTypeIs(node.NodeTypeId, "EmptyElemTag"u8)))
            {
                contentStart = node.EndByte;
                sawStart = true;
                if (ast.NodeTypeIs(node.NodeTypeId, "EmptyElemTag"u8))
                    return "";
            }
            else if (ast.NodeTypeIs(node.NodeTypeId, "ETag"u8))
            {
                contentEnd = node.StartByte;
                break;
            }
        }

        if (!sawStart || contentEnd <= contentStart) return "";
        return WebUtility.HtmlDecode(DecodeSpan(utf8, contentStart, contentEnd)).Trim();
    }

    private static string DecodeSpan(byte[] utf8, uint start, uint end)
    {
        if (end < start || end > (uint)utf8.Length) return "";
        return Encoding.UTF8.GetString(
            utf8,
            checked((int)start),
            checked((int)(end - start)));
    }

    private static void ApplyField(PlayerBuilder player, string field, string value)
    {
        switch (field.ToLowerInvariant())
        {
            case "fideid": player.FideId = value; break;
            case "name": player.Name = value; break;
            case "country": player.Federation = value; break;
            case "sex": player.Sex = value; break;
            case "title": player.Title = value; break;
            case "rating": player.Standard = Int(value); break;
            case "rapid_rating": player.Rapid = Int(value); break;
            case "blitz_rating": player.Blitz = Int(value); break;
            case "birthday": player.Birthday = value; break;
            case "flag": player.Flag = value; break;
        }
    }

    private static byte[] ReadBounded(Stream input, long maximum, string label, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var block = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = input.Read(block, 0, block.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += read;
            if (total > maximum)
                throw new InvalidDataException(
                    $"{label} exceeded the {maximum} byte safety cap while reading.");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private static FidePlayerCandidate ToCandidate(Player p)
        => new(p.FideId, p.Name, p.Title, p.Federation,
            p.Standard, p.Rapid, p.Blitz, p.BirthYear, null);

    private static int CandidateScore(string canonicalQuery, string candidate)
    {
        string name = PlayerAlias.Canonical(candidate);
        if (canonicalQuery == name) return 0;
        if (canonicalQuery.Replace(" ", "", StringComparison.Ordinal)
            == name.Replace(" ", "", StringComparison.Ordinal)) return 1;
        string[] tokens = canonicalQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] nameTokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && tokens.All(token => nameTokens.Contains(token, StringComparer.Ordinal))) return 2;
        if (name.Contains(canonicalQuery, StringComparison.Ordinal)) return 3;
        return 10;
    }

    private static string Mode(string cohort)
        => cohort.EndsWith("_rapid", StringComparison.Ordinal) ? "rapid"
            : cohort.EndsWith("_blitz", StringComparison.Ordinal) ? "blitz"
            : "standard";

    private static int RatingFor(Player p, string mode)
        => mode == "rapid" ? p.Rapid : mode == "blitz" ? p.Blitz : p.Standard;

    private static bool IsInactive(string flag)
        => flag.Contains('I') || flag.Contains('i');

    private static int Int(string value)
        => int.TryParse(value, out int parsed) ? parsed : 0;

    private static int BirthYear(string value)
    {
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return digits.Length >= 4 && int.TryParse(digits[..4], out int year) ? year : 0;
    }

    private static string NormalizeTitle(string value)
        => value.Trim().ToUpperInvariant() switch
        {
            "G" => "GM",
            "M" => "IM",
            "F" => "FM",
            "C" => "CM",
            "WG" => "WGM",
            "WM" => "WIM",
            "WF" => "WFM",
            "WC" => "WCM",
            var title => title,
        };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Laplace-Chess-Ingest/1.0");
        return client;
    }
}
