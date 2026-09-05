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

    // Projection batches are physical work units only. Keep them small enough that several
    // independent native grammar parses can run concurrently without retaining the expanded
    // publication in memory. The source order is restored by batch ordinal after projection.
    private const int MaxPlayersPerGrammarBatch = 4096;
    private const int MaxGrammarBatchBytes = 8 * 1024 * 1024;
    private const int MaxProjectionWorkers = 8;

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(6);
    private static readonly SemaphoreSlim EstateGate = new(1, 1);
    private static readonly object RefreshSync = new();
    private static readonly HttpClient Http = CreateClient();

    private static ReadOnlySpan<byte> PlayerOpenTag => "<player>"u8;
    private static ReadOnlySpan<byte> PlayerCloseTag => "</player>"u8;

    private static Player[]? _players;
    private static FideRatingIndex? _index;
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

        await GetPlayersAsync(ct).ConfigureAwait(false);
        var index = _index ?? throw new InvalidOperationException("FIDE estate index was not published.");
        string canonical = PlayerAlias.Canonical(query);
        int cap = Math.Clamp(limit, 1, 100);
        return await Task.Run(
            () => SearchPlayers(index, canonical, cap, ct),
            ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<FidePlayerCandidate>> TopAsync(
        string cohort, int limit, CancellationToken ct)
    {
        cohort = cohort.Trim().ToLowerInvariant();
        if (!ChessGameFetcher.FideCohorts.Contains(cohort))
            throw new ArgumentException($"unknown FIDE cohort '{cohort}'", nameof(cohort));

        await GetPlayersAsync(ct).ConfigureAwait(false);
        var index = _index ?? throw new InvalidOperationException("FIDE estate index was not published.");
        return await Task.Run(
            () => TopPlayers(index, cohort, limit, DateTime.UtcNow.Year, ct),
            ct).ConfigureAwait(false);
    }

    public static async Task<FidePlayerCandidate> FindByIdAsync(
        string fideId, CancellationToken ct)
    {
        fideId = fideId.Trim();
        if (fideId.Length is < 4 or > 12 || !fideId.All(char.IsDigit))
            throw new ArgumentException("FIDE identifier must contain 4 to 12 digits.", nameof(fideId));

        await GetPlayersAsync(ct).ConfigureAwait(false);
        var index = _index ?? throw new InvalidOperationException("FIDE estate index was not published.");
        return index.TryFindById(fideId, out var player)
            ? ToCandidate(player)
            : throw new KeyNotFoundException($"FIDE id {fideId} is absent from the official rating list.");
    }

    internal static IReadOnlyList<FidePlayerCandidate> SearchXml(
        Stream xml, string query, int limit, CancellationToken ct = default, int? parseWorkers = null)
    {
        var players = ParsePlayers(xml, MaxXmlBytes, ct, parseWorkers);
        return SearchPlayers(
            new FideRatingIndex(players),
            PlayerAlias.Canonical(query.Trim()),
            Math.Clamp(limit, 1, 100),
            ct);
    }

    internal static IReadOnlyList<FidePlayerCandidate> TopXml(
        Stream xml, string cohort, int limit, int currentYear,
        CancellationToken ct = default, int? parseWorkers = null)
    {
        var players = ParsePlayers(xml, MaxXmlBytes, ct, parseWorkers);
        return TopPlayers(
            new FideRatingIndex(players),
            cohort.Trim().ToLowerInvariant(), limit, currentYear, ct);
    }

    internal static IReadOnlyList<FidePlayerCandidate> SearchArchive(
        byte[] archive, string query, int limit, CancellationToken ct = default, int? parseWorkers = null)
    {
        var players = ParseArchive(archive, ct, parseWorkers);
        return SearchPlayers(
            new FideRatingIndex(players),
            PlayerAlias.Canonical(query.Trim()),
            Math.Clamp(limit, 1, 100),
            ct);
    }

    internal static IReadOnlyList<FidePlayerCandidate> TopArchive(
        byte[] archive, string cohort, int limit, int currentYear,
        CancellationToken ct = default, int? parseWorkers = null)
    {
        var players = ParseArchive(archive, ct, parseWorkers);
        return TopPlayers(
            new FideRatingIndex(players),
            cohort.Trim().ToLowerInvariant(), limit, currentYear, ct);
    }

    internal static Player[] ProjectXml(
        Stream xml, int parseWorkers, CancellationToken ct = default)
        => ParsePlayers(xml, MaxXmlBytes, ct, parseWorkers);

    internal static string? LastRefreshError => _lastRefreshError;
    internal static DateTimeOffset CachedFetchedAt => _playersFetchedAt;

    private static IReadOnlyList<FidePlayerCandidate> SearchPlayers(
        FideRatingIndex index, string canonical, int cap, CancellationToken ct)
    {
        string compact = canonical.Replace(" ", "", StringComparison.Ordinal);
        string[] queryTokens = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int seen = 0;

        return index.Names
            .Select(entry =>
            {
                if ((++seen & 4095) == 0) ct.ThrowIfCancellationRequested();
                return (Entry: entry, Score: CandidateScore(canonical, compact, queryTokens, entry));
            })
            .Where(static x => x.Score < 10)
            .OrderBy(static x => x.Score)
            .ThenByDescending(static x => x.Entry.Player.Standard)
            .ThenBy(static x => x.Entry.Player.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static x => x.Entry.Player.FideId, StringComparer.Ordinal)
            .Take(cap)
            .Select(static x => ToCandidate(x.Entry.Player))
            .ToArray();
    }

    private static IReadOnlyList<FidePlayerCandidate> TopPlayers(
        FideRatingIndex index, string cohort, int limit, int currentYear, CancellationToken ct)
    {
        int cap = Math.Clamp(limit, 1, 100);
        string mode = Mode(cohort);
        bool women = cohort.StartsWith("women", StringComparison.Ordinal)
            || cohort.StartsWith("girls", StringComparison.Ordinal);
        bool junior = cohort.StartsWith("juniors", StringComparison.Ordinal)
            || cohort.StartsWith("girls", StringComparison.Ordinal);
        int juniorBirthFloor = currentYear - 20;
        int seen = 0;
        var result = new List<FidePlayerCandidate>(cap);

        // The expensive rating sort is already materialized once in FideRatingIndex. Walk the
        // requested plane until enough eligible cohort members have been found.
        foreach (var player in index.Ranked(mode))
        {
            if ((++seen & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (IsInactive(player.Flag)) continue;
            if (women && !player.Sex.Equals("F", StringComparison.OrdinalIgnoreCase)) continue;
            if (junior && player.BirthYear < juniorBirthFloor) continue;

            result.Add(ToCandidate(player) with { Rank = result.Count + 1 });
            if (result.Count >= cap) break;
        }

        return result;
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
        // Build every immutable index before publishing the generation pointer. Readers that
        // observe _players are therefore guaranteed to observe an index for the same estate.
        var index = new FideRatingIndex(players);
        _index = index;
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

    private static Player[] ParseArchive(
        byte[] archive, CancellationToken ct, int? parseWorkers = null)
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
        return ParsePlayers(input, entry.Length, ct, parseWorkers);
    }

    /// <summary>
    /// The source framer recognizes only the provider's repeated record boundary;
    /// native projection owns the fields. Independent grammar batches are projected on
    /// topology-sized CPU workers, while the number of live batch buffers remains bounded.
    /// Source order is restored by batch ordinal before domain normalization.
    /// </summary>
    private static Player[] ParsePlayers(
        Stream xml, long maximumBytes, CancellationToken ct, int? parseWorkers = null)
    {
        int workerCount = Math.Clamp(
            parseWorkers ?? CpuTopology.ResolveCpuBoundWorkers(),
            1,
            MaxProjectionWorkers);

        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = pipelineCts.Token;
        var projectedBatches = new Dictionary<int, PlayerBuilder[]>();
        var inFlight = new List<(int Ordinal, Task<PlayerBuilder[]> Work)>(workerCount);
        using var batch = new MemoryStream(MaxGrammarBatchBytes);
        var block = new byte[1024 * 1024];
        byte[] carry = [];
        long total = 0;
        int batchCount = 0;
        int batchOrdinal = 0;

        void DrainOne()
        {
            token.ThrowIfCancellationRequested();
            Task<PlayerBuilder[]> completed = Task.WhenAny(inFlight.Select(static x => x.Work))
                .WaitAsync(token).GetAwaiter().GetResult();
            int index = inFlight.FindIndex(x => ReferenceEquals(x.Work, completed));
            if (index < 0) throw new InvalidOperationException("FIDE projection worker result was lost.");
            var item = inFlight[index];
            projectedBatches[item.Ordinal] = item.Work.GetAwaiter().GetResult();
            inFlight.RemoveAt(index);
        }

        void FlushBatch()
        {
            if (batchCount == 0) return;
            byte[] payload = batch.ToArray();
            int ordinal = batchOrdinal++;
            inFlight.Add((ordinal, Task.Run(
                () => ParsePlayerProjectionBatch(payload, token), token)));
            batch.SetLength(0);
            batchCount = 0;
            if (inFlight.Count >= workerCount) DrainOne();
        }

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
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
                        FlushBatch();

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

            FlushBatch();
            while (inFlight.Count > 0) DrainOne();
        }
        catch
        {
            pipelineCts.Cancel();
            try
            {
                Task.WhenAll(inFlight.Select(static x => x.Work)).GetAwaiter().GetResult();
            }
            catch
            {
                // The initiating parser/worker exception remains the failure contract.
            }
            throw;
        }

        var builders = new List<PlayerBuilder>();
        for (int ordinal = 0; ordinal < batchOrdinal; ordinal++)
        {
            if (!projectedBatches.TryGetValue(ordinal, out var projected))
                throw new InvalidDataException($"FIDE projection omitted batch {ordinal}.");
            builders.AddRange(projected);
        }

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

    private static PlayerBuilder[] ParsePlayerProjectionBatch(
        byte[] records, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var projected = new FidePlayerProjection[MaxPlayersPerGrammarBatch];
        int count = FideXmlProjection.Project(records, projected);
        var players = new PlayerBuilder[count];
        for (int i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            ref readonly FidePlayerProjection p = ref projected[i];
            players[i] = new PlayerBuilder
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
            };
        }
        return players;
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

    private static int CandidateScore(
        string canonicalQuery,
        string compactQuery,
        string[] queryTokens,
        FideRatingIndex.NameEntry candidate)
    {
        if (canonicalQuery == candidate.CanonicalName) return 0;
        if (compactQuery == candidate.CompactCanonicalName) return 1;
        if (queryTokens.Length > 0
            && queryTokens.All(token => candidate.Tokens.Contains(token, StringComparer.Ordinal))) return 2;
        if (candidate.CanonicalName.Contains(canonicalQuery, StringComparison.Ordinal)) return 3;
        return 10;
    }

    private static string Mode(string cohort)
        => cohort.EndsWith("_rapid", StringComparison.Ordinal) ? "rapid"
            : cohort.EndsWith("_blitz", StringComparison.Ordinal) ? "blitz"
            : "standard";

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
