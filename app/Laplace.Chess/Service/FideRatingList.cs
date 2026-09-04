using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// Reads FIDE's first-party combined rating artifact. Search and roster discovery
/// belong to the published player estate; profile HTML is enrichment for a selected
/// identity, not the authority for discovering whether the identity exists.
///
/// Native code owns the deterministic projection from the provider's repeated XML
/// records. This class streams the compressed estate, bounds batches, and maps those
/// projected byte spans into the chess domain. The successful projection is persisted
/// as derived provider state so interactive reads never have to rebuild the publication
/// merely because the API process restarted.
/// </summary>
internal static class FideRatingList
{
    internal const string XmlArchiveUrl = "https://ratings.fide.com/download/players_list_xml.zip";
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private const long MaxXmlBytes = 1024L * 1024 * 1024;
    // The published estate currently contains well over a hundred thousand compact records.
    // Keep both dimensions bounded while amortizing native projection calls.
    private const int MaxPlayersPerGrammarBatch = 32 * 1024;
    private const int MaxGrammarBatchBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim EstateGate = new(1, 1);
    private static readonly object RefreshSync = new();
    private static readonly HttpClient Http = CreateClient();

    private static ReadOnlySpan<byte> PlayerOpenTag => "<player>"u8;
    private static ReadOnlySpan<byte> PlayerCloseTag => "</player>"u8;

    private static Player[]? _players;
    private static IReadOnlyDictionary<string, Player>? _playersById;
    private static DateTimeOffset _playersFetchedAt;
    private static Task? _refreshTask;
    private static string? _lastRefreshError;

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

    public static async Task<FidePlayerCandidate> FindByIdAsync(
        string fideId, CancellationToken ct)
    {
        fideId = fideId.Trim();
        if (fideId.Length is < 4 or > 12 || !fideId.All(char.IsDigit))
            throw new ArgumentException("FIDE identifier must contain 4 to 12 digits.", nameof(fideId));

        var players = await GetPlayersAsync(ct).ConfigureAwait(false);
        var byId = _playersById;
        Player? player = byId is not null && byId.TryGetValue(fideId, out var indexed)
            ? indexed
            : players.FirstOrDefault(p => p.FideId.Equals(fideId, StringComparison.Ordinal));
        return player is null
            ? throw new KeyNotFoundException($"FIDE id {fideId} is absent from the official rating list.")
            : ToCandidate(player);
    }

    internal static IReadOnlyList<FidePlayerCandidate> SearchXml(
        Stream xml, string query, int limit, CancellationToken ct = default)
    {
        var players = ParsePlayers(xml, MaxXmlBytes, ct);
        return SearchPlayers(
            players,
            PlayerAlias.Canonical(query.Trim()),
            Math.Clamp(limit, 1, 100),
            ct);
    }

    internal static IReadOnlyList<FidePlayerCandidate> TopXml(
        Stream xml, string cohort, int limit, int currentYear, CancellationToken ct = default)
    {
        var players = ParsePlayers(xml, MaxXmlBytes, ct);
        return TopPlayers(players, cohort.Trim().ToLowerInvariant(), limit, currentYear, ct);
    }

    internal static IReadOnlyList<FidePlayerCandidate> SearchArchive(
        byte[] archive, string query, int limit, CancellationToken ct = default)
    {
        var players = ParseArchive(archive, ct);
        return SearchPlayers(
            players,
            PlayerAlias.Canonical(query.Trim()),
            Math.Clamp(limit, 1, 100),
            ct);
    }

    internal static IReadOnlyList<FidePlayerCandidate> TopArchive(
        byte[] archive, string cohort, int limit, int currentYear, CancellationToken ct = default)
    {
        var players = ParseArchive(archive, ct);
        return TopPlayers(players, cohort.Trim().ToLowerInvariant(), limit, currentYear, ct);
    }

    internal static string? LastRefreshError => _lastRefreshError;
    internal static DateTimeOffset CachedFetchedAt => _playersFetchedAt;

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
        var cached = Volatile.Read(ref _players);
        if (cached is not null)
        {
            if (now - _playersFetchedAt >= RefreshAfter)
                EnsureBackgroundRefresh();
            return cached;
        }

        await EstateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            cached = Volatile.Read(ref _players);
            if (cached is not null)
            {
                if (now - _playersFetchedAt >= RefreshAfter)
                    EnsureBackgroundRefresh();
                return cached;
            }

            FideRatingSnapshot.Loaded? snapshot = null;
            try
            {
                snapshot = await FideRatingSnapshot.TryLoadAsync(
                    FideRatingSnapshot.DefaultPath, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _lastRefreshError = $"FIDE snapshot read failed: {ex.Message}";
            }
            catch (UnauthorizedAccessException ex)
            {
                _lastRefreshError = $"FIDE snapshot read failed: {ex.Message}";
            }

            if (snapshot is not null)
            {
                PublishPlayers(snapshot.Players, snapshot.FetchedAt);
                if (now - snapshot.FetchedAt >= RefreshAfter)
                    EnsureBackgroundRefresh();
                return snapshot.Players;
            }

            return await RefreshUnderGateAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            EstateGate.Release();
        }
    }

    private static void EnsureBackgroundRefresh()
    {
        lock (RefreshSync)
        {
            if (_refreshTask is { IsCompleted: false })
                return;

            _refreshTask = Task.Run(async () =>
            {
                await EstateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await RefreshUnderGateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException
                    or IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or TaskCanceledException)
                {
                    // Stale-while-refresh: the last valid publication remains live. The
                    // error is retained for diagnostics rather than poisoning readers.
                    _lastRefreshError = $"FIDE background refresh failed: {ex.Message}";
                }
                finally
                {
                    EstateGate.Release();
                }
            });
        }
    }

    private static async Task<Player[]> RefreshUnderGateAsync(CancellationToken ct)
    {
        DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;
        byte[] archive = await DownloadArchiveAsync(ct).ConfigureAwait(false);
        string archiveSha256 = Convert.ToHexString(SHA256.HashData(archive));
        var players = await Task.Run(() => ParseArchive(archive, ct), ct).ConfigureAwait(false);
        if (players.Length == 0)
            throw new InvalidDataException(
                "FIDE published rating artifact parsed successfully but contained no valid player records.");

        string? persistenceError = null;
        try
        {
            await FideRatingSnapshot.SaveAsync(
                FideRatingSnapshot.DefaultPath,
                players,
                fetchedAt,
                archiveSha256,
                ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            persistenceError = $"FIDE snapshot persist failed: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            persistenceError = $"FIDE snapshot persist failed: {ex.Message}";
        }

        PublishPlayers(players, fetchedAt);
        _lastRefreshError = persistenceError;
        return players;
    }

    private static void PublishPlayers(Player[] players, DateTimeOffset fetchedAt)
    {
        // Provider ids are supposed to be unique. Failing here is preferable to letting
        // an ambiguous publication make exact-id reads depend on array order.
        var byId = players.ToDictionary(static p => p.FideId, StringComparer.Ordinal);
        _playersById = byId;
        _playersFetchedAt = fetchedAt;
        Volatile.Write(ref _players, players);
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

    private static Player[] ParseArchive(byte[] archive, CancellationToken ct)
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
        return ParsePlayers(input, entry.Length, ct);
    }

    /// <summary>
    /// The source framer recognizes only the provider's repeated record boundary;
    /// native projection owns the fields. Bounded batches keep memory and cancellation
    /// latency independent of the size of the monthly publication.
    /// </summary>
    private static Player[] ParsePlayers(Stream xml, long maximumBytes, CancellationToken ct)
    {
        var builders = new List<PlayerBuilder>();
        using var batch = new MemoryStream(MaxGrammarBatchBytes);
        var block = new byte[1024 * 1024];
        byte[] carry = [];
        long total = 0;
        int batchCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = xml.Read(block, 0, block.Length);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException(
                    $"FIDE XML player list exceeded the {maximumBytes} byte safety cap while reading.");

            var window = new byte[checked(carry.Length + read)];
            carry.CopyTo(window, 0);
            block.AsSpan(0, read).CopyTo(window.AsSpan(carry.Length));

            int scan = 0;
            int keepFrom = Math.Max(0, window.Length - PlayerOpenTag.Length + 1);
            while (scan < window.Length)
            {
                int relativeStart = window.AsSpan(scan).IndexOf(PlayerOpenTag);
                if (relativeStart < 0) break;
                int recordStart = scan + relativeStart;
                int closeSearchStart = checked(recordStart + PlayerOpenTag.Length);
                int relativeClose = window.AsSpan(closeSearchStart).IndexOf(PlayerCloseTag);
                if (relativeClose < 0)
                {
                    keepFrom = recordStart;
                    break;
                }

                int recordEnd = checked(closeSearchStart + relativeClose + PlayerCloseTag.Length);
                int recordLength = recordEnd - recordStart;
                if (recordLength > MaxGrammarBatchBytes)
                    throw new InvalidDataException("FIDE XML player record exceeds the grammar batch cap.");

                if (batchCount > 0
                    && (batchCount >= MaxPlayersPerGrammarBatch
                        || batch.Length + recordLength > MaxGrammarBatchBytes))
                {
                    ParsePlayerProjectionBatch(
                        batch.GetBuffer().AsSpan(0, checked((int)batch.Length)), builders, ct);
                    batch.SetLength(0);
                    batchCount = 0;
                }

                batch.Write(window, recordStart, recordLength);
                batchCount++;
                scan = recordEnd;
                keepFrom = Math.Max(scan, window.Length - PlayerOpenTag.Length + 1);
            }

            int carryLength = window.Length - keepFrom;
            if (carryLength > MaxGrammarBatchBytes)
                throw new InvalidDataException("FIDE XML player record is missing its closing tag.");
            carry = carryLength == 0 ? [] : window.AsSpan(keepFrom).ToArray();
        }

        if (carry.AsSpan().IndexOf(PlayerOpenTag) >= 0)
            throw new InvalidDataException("FIDE XML player record is missing its closing tag.");
        if (batchCount > 0)
            ParsePlayerProjectionBatch(
                batch.GetBuffer().AsSpan(0, checked((int)batch.Length)), builders, ct);

        if (builders.Count == 0)
            throw new InvalidDataException(
                "FIDE XML player list did not contain canonical <player> records.");

        return builders
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

    private static void ParsePlayerProjectionBatch(
        ReadOnlySpan<byte> records, List<PlayerBuilder> players, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var projected = new FidePlayerProjection[MaxPlayersPerGrammarBatch];
        int count = FideXmlProjection.Project(records, projected);
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            ref readonly FidePlayerProjection p = ref projected[i];
            players.Add(new PlayerBuilder
            {
                FideId = Text(records, p.FideId),
                Name = Text(records, p.Name),
                Federation = Text(records, p.Country),
                Sex = Text(records, p.Sex),
                Title = Text(records, p.Title),
                Standard = Int(Text(records, p.StandardRating)),
                Rapid = Int(Text(records, p.RapidRating)),
                Blitz = Int(Text(records, p.BlitzRating)),
                Birthday = Text(records, p.Birthday),
                Flag = Text(records, p.Flag),
            });
        }
    }

    private static string Text(ReadOnlySpan<byte> utf8, NativeTextSpan span)
    {
        int offset = checked((int)span.Offset);
        int length = checked((int)span.Length);
        if (offset < 0 || length < 0 || offset > utf8.Length - length)
            throw new InvalidDataException("native FIDE XML projection returned an invalid byte span");
        return WebUtility.HtmlDecode(Encoding.UTF8.GetString(utf8.Slice(offset, length))).Trim();
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
