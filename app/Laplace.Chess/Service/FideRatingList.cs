using System.IO.Compression;
using System.Net.Http.Headers;
using System.Xml;

namespace Laplace.Chess.Service;

/// <summary>
/// Reads FIDE's first-party combined rating artifact. Search and roster discovery
/// belong to the published player estate; profile HTML is enrichment for a selected
/// identity, not the authority for discovering whether the identity exists.
/// </summary>
internal static class FideRatingList
{
    internal const string XmlArchiveUrl = "https://ratings.fide.com/download/players_list_xml.zip";
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private const long MaxXmlBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim ArchiveGate = new(1, 1);
    private static readonly HttpClient Http = CreateClient();

    private static byte[]? _archive;
    private static DateTimeOffset _archiveFetchedAt;

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

    public static async Task<IReadOnlyList<FidePlayerCandidate>> SearchAsync(
        string query, int limit, CancellationToken ct)
    {
        byte[] archive = await GetArchiveAsync(ct).ConfigureAwait(false);
        return await Task.Run(
            () => SearchArchive(archive, query, limit, ct),
            ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<FidePlayerCandidate>> TopAsync(
        string cohort, int limit, CancellationToken ct)
    {
        byte[] archive = await GetArchiveAsync(ct).ConfigureAwait(false);
        return await Task.Run(
            () => TopArchive(archive, cohort, limit, DateTime.UtcNow.Year, ct),
            ct).ConfigureAwait(false);
    }

    internal static IReadOnlyList<FidePlayerCandidate> SearchXml(
        Stream xml, string query, int limit, CancellationToken ct = default)
    {
        string canonical = PlayerAlias.Canonical(query);
        int cap = Math.Clamp(limit, 1, 100);
        return ReadPlayers(xml, ct)
            .Where(p => CandidateScore(canonical, p.Name) < 10)
            .OrderBy(p => CandidateScore(canonical, p.Name))
            .ThenByDescending(p => p.Standard)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.FideId, StringComparer.Ordinal)
            .Take(cap)
            .Select(ToCandidate)
            .ToArray();
    }

    internal static IReadOnlyList<FidePlayerCandidate> TopXml(
        Stream xml, string cohort, int limit, int currentYear, CancellationToken ct = default)
    {
        int cap = Math.Clamp(limit, 1, 100);
        var mode = Mode(cohort);
        bool women = cohort.StartsWith("women", StringComparison.Ordinal)
            || cohort.StartsWith("girls", StringComparison.Ordinal);
        bool junior = cohort.StartsWith("juniors", StringComparison.Ordinal)
            || cohort.StartsWith("girls", StringComparison.Ordinal);
        int juniorBirthFloor = currentYear - 20;

        var ranked = ReadPlayers(xml, ct)
            .Where(p => !IsInactive(p.Flag))
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
        return ranked;
    }

    internal static IReadOnlyList<FidePlayerCandidate> SearchArchive(
        byte[] archive, string query, int limit, CancellationToken ct = default)
    {
        using var xml = OpenXml(archive);
        return SearchXml(xml, query, limit, ct);
    }

    internal static IReadOnlyList<FidePlayerCandidate> TopArchive(
        byte[] archive, string cohort, int limit, int currentYear, CancellationToken ct = default)
    {
        using var xml = OpenXml(archive);
        return TopXml(xml, cohort, limit, currentYear, ct);
    }

    private static async Task<byte[]> GetArchiveAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cached = _archive;
        if (cached is not null && now - _archiveFetchedAt < RefreshAfter)
            return cached;

        await ArchiveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            cached = _archive;
            if (cached is not null && now - _archiveFetchedAt < RefreshAfter)
                return cached;

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
            int read;
            long total = 0;
            while ((read = await stream.ReadAsync(block.AsMemory(0, block.Length), ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxArchiveBytes)
                    throw new InvalidDataException(
                        $"FIDE rating archive exceeded the {MaxArchiveBytes} byte safety cap while reading.");
                buffer.Write(block, 0, read);
            }
            var bytes = buffer.ToArray();
            using (OpenXml(bytes)) { }
            _archive = bytes;
            _archiveFetchedAt = now;
            return bytes;
        }
        finally
        {
            ArchiveGate.Release();
        }
    }

    private static Stream OpenXml(byte[] archive)
    {
        var backing = new MemoryStream(archive, writable: false);
        ZipArchive? zip = null;
        try
        {
            zip = new ZipArchive(backing, ZipArchiveMode.Read, leaveOpen: false);
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
            return new ZipEntryStream(zip, backing, entry.Open());
        }
        catch
        {
            zip?.Dispose();
            backing.Dispose();
            throw;
        }
    }

    private static IEnumerable<Player> ReadPlayers(Stream xml, CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CloseInput = false,
        };
        using var reader = XmlReader.Create(xml, settings);
        int seen = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !reader.LocalName.Equals("player", StringComparison.OrdinalIgnoreCase))
                continue;
            if ((++seen & 4095) == 0) ct.ThrowIfCancellationRequested();

            string fideId = "", name = "", federation = "", sex = "", title = "", birthday = "", flag = "";
            int standard = 0, rapid = 0, blitz = 0;
            using var subtree = reader.ReadSubtree();
            while (subtree.Read())
            {
                if (subtree.NodeType != XmlNodeType.Element || subtree.IsEmptyElement) continue;
                string field = subtree.LocalName;
                if (field.Equals("player", StringComparison.OrdinalIgnoreCase)) continue;
                string value = subtree.ReadString().Trim();
                switch (field)
                {
                    case "fideid": fideId = value; break;
                    case "name": name = value; break;
                    case "country": federation = value; break;
                    case "sex": sex = value; break;
                    case "title": title = value; break;
                    case "rating": standard = Int(value); break;
                    case "rapid_rating": rapid = Int(value); break;
                    case "blitz_rating": blitz = Int(value); break;
                    case "birthday": birthday = value; break;
                    case "flag": flag = value; break;
                }
            }

            if (fideId.Length is < 4 or > 12 || !fideId.All(char.IsDigit) || string.IsNullOrWhiteSpace(name))
                continue;
            yield return new Player(
                fideId, name.Trim(), federation.Trim().ToUpperInvariant(), sex.Trim().ToUpperInvariant(),
                NormalizeTitle(title), standard, rapid, blitz, BirthYear(birthday), flag.Trim());
        }
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

    private sealed class ZipEntryStream(ZipArchive zip, Stream backing, Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                zip.Dispose();
                backing.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
