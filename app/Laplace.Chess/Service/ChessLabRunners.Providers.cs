using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Microsoft.Extensions.Logging;

namespace Laplace.Chess.Service;

public static partial class ChessLabRunners
{
    public static async Task RunLichessBotAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string? token = LichessBot.ResolveToken(Config(slot.Job.Config, "token", ""));
        if (string.IsNullOrEmpty(token))
        {
            lab.Publish(slot, new ChessLabLogEvent("error", "LICHESS_API token missing"));
            Finish(lab, slot, ChessLabJobState.Failed, "no token");
            return;
        }
        int maxConcurrent = int.Parse(Config(slot.Job.Config, "maxConcurrent",
            LichessDefaults.MaxConcurrent.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var host = await lab.GetLiveHostAsync(ct);

        await using var bot = new LichessBot(
            token,
            host,
            engine: Config(slot.Job.Config, "engine", "") is { Length: > 0 } engineName
                ? UciLichessEngine.Create(engineName, new Dictionary<string, string>()) : null,
            record: true,
            log: new LabLogger(lab, slot));
        lab.Publish(slot, new ChessLabLogEvent("info", "lichess bot starting (engine through laplace-uci, recording)"));
        await bot.RunAsync(maxConcurrent, ct);
        Finish(lab, slot, ChessLabJobState.Cancelled, "stopped");
    }

    public static async Task RunLichessFetchAsync(ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string user = Config(slot.Job.Config, "user", "").Trim();
        string site = Config(slot.Job.Config, "site", "lichess");
        bool all = bool.TryParse(Config(slot.Job.Config, "all", "true"), out bool allValue) && allValue;
        bool ingest = bool.TryParse(Config(slot.Job.Config, "ingest", "true"), out bool ingestValue) && ingestValue;
        bool persistPgn = ChessLabStorage.PersistPgn(slot.Job.Config, defaultValue: !ingest);
        int? max = ChessGameFetcher.ResolveArchiveLimit(all, Config(slot.Job.Config, "max", ""));
        string fideId = Config(slot.Job.Config, "fideId", "").Trim();
        if (user.Length == 0) throw new ArgumentException("A provider username is required.");

        var online = await ChessGameFetcher.FetchProfileAsync(user, site, ct);
        var profiles = new List<ChessPlayerProfile> { online };
        if (fideId.Length > 0)
            profiles.Add(await ChessGameFetcher.FetchProfileAsync(fideId, "fide", ct));
        lab.Publish(slot, ProfileTable(profiles));
        await AddProfileArtifactsAsync(lab, slot, profiles, ct);

        ChessPgnIngestor? ingestor = null;
        ChessLabIngestRun? journal = null;
        ChessPgnIngestor.ProfileResult profileResult = default;
        string? spoolDir = null;
        string? outPath = null;
        bool completed = false;
        try
        {
            if (ingest)
            {
                var liveHost = await lab.GetLiveHostAsync(ct);
                // A web import is an ingest run like `laplace-mono ingest chess`: the same
                // run/file journal rows and one terminal status (with its error) per job.
                journal = await ChessLabIngestRun.StartAsync(liveHost.DataSource, lab.Log, slot.Job.Id,
                    $"{ChessGameFetcher.Sanitize(site.Trim().ToLowerInvariant())}/{ChessGameFetcher.Sanitize(online.ProviderId)}", ct);
                lab.Publish(slot, new ChessLabLogEvent("info",
                    $"ingest run {journal.RunId} ({ChessLabIngestRun.SourceName}) journaled"));
                ingestor = await ChessPgnIngestor.AttachAsync(liveHost, ct);
                journal.Attach(ingestor);
                profileResult = await ingestor.IngestPlayerProfilesAsync(profiles, ct);
                lab.Publish(slot, new ChessLabMetricEvent("profiles_ingested", profileResult.Profiles));
                lab.Publish(slot, new ChessLabMetricEvent("identity_links", profileResult.Links));
            }

            string fileName = $"{ChessGameFetcher.Sanitize(online.ProviderId)}_{ChessGameFetcher.Sanitize(site)}.pgn";
            if (persistPgn)
            {
                outPath = Path.Combine(LabDir, slot.Job.Id, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            }
            else
            {
                spoolDir = ChessLabStorage.CreateJobSpool(slot.Job.Id);
                outPath = Path.Combine(spoolDir, fileName);
            }

            int parsedWhileFetching = 0;
            int novelWhileFetching = 0;
            int appliedWhileFetching = 0;
            int repairedWhileFetching = 0;
            int verifiedWhileFetching = 0;
            long verifiedPliesWhileFetching = 0;
            int games = await ChessGameFetcher.FetchAsync(online.ProviderId, site, max, 0, outPath,
                msg => lab.Publish(slot, new ChessLabLogEvent("info", msg)), ct,
                ingest && site.Trim().Equals("chesscom", StringComparison.OrdinalIgnoreCase)
                    ? async (archive, batch, token) =>
                    {
                        var part = await journal!.IngestUnitAsync(archive, batch.Count, () => ingestor!.IngestGamesAsync(
                            batch, $"Chess.com monthly archive {archive}",
                            msg => lab.Publish(slot, new ChessLabLogEvent("info", msg)), token,
                            requireCompleteSource: true));
                        parsedWhileFetching += part.Parsed;
                        novelWhileFetching += part.Novel;
                        appliedWhileFetching += part.Applied;
                        repairedWhileFetching += part.Repaired;
                        verifiedWhileFetching += part.Verified;
                        verifiedPliesWhileFetching += part.VerifiedPlies;
                        lab.UpdateSummary(slot, new ChessLabJobSummary(
                            appliedWhileFetching, 0, $"{appliedWhileFetching} new games recorded while downloading"));
                    }
                    : null);
            lab.Publish(slot, new ChessLabMetricEvent("games_fetched", games));

            if (ingest)
            {
                bool chessCom = site.Trim().Equals("chesscom", StringComparison.OrdinalIgnoreCase);
                var gameResult = chessCom
                    ? new ChessPgnIngestor.Result(
                        parsedWhileFetching, novelWhileFetching, appliedWhileFetching,
                        repairedWhileFetching, verifiedWhileFetching, verifiedPliesWhileFetching)
                    : await journal!.IngestUnitAsync(fileName, games, () => ingestor!.IngestFileAsync(outPath,
                        msg => lab.Publish(slot, new ChessLabLogEvent("info", msg)), ct,
                        requireCompleteSource: true));
                if (gameResult.Parsed != games)
                    throw new InvalidDataException(
                        $"provider returned {games} games but only {gameResult.Parsed} complete legal games validated");
                if (gameResult.Verified != games)
                    throw new InvalidDataException(
                        $"provider returned {games} games but exact persisted readback verified only {gameResult.Verified}");
                lab.Publish(slot, new ChessLabMetricEvent("games_validated", gameResult.Parsed));
                lab.Publish(slot, new ChessLabMetricEvent("games_ingested", gameResult.Applied));
                lab.Publish(slot, new ChessLabMetricEvent("games_repaired", gameResult.Repaired));
                lab.Publish(slot, new ChessLabMetricEvent("games_persisted_verified", gameResult.Verified));
                lab.Publish(slot, new ChessLabMetricEvent("plies_persisted_verified", gameResult.VerifiedPlies));

                long pgnBytes = new FileInfo(outPath).Length;
                string pgnSha256;
                await using (var pgnStream = File.OpenRead(outPath))
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                    pgnSha256 = Convert.ToHexStringLower(await sha256.ComputeHashAsync(pgnStream, ct));

                string proofPath = Path.Combine(LabDir, slot.Job.Id, "recording-proof.json");
                Directory.CreateDirectory(Path.GetDirectoryName(proofPath)!);
                await File.WriteAllTextAsync(proofPath, JsonSerializer.Serialize(new
                {
                    schema = "laplace.chess-provider-recording/v1",
                    jobId = slot.Job.Id,
                    provider = site.Trim().ToLowerInvariant(),
                    providerUser = online.ProviderId,
                    sourcePgnBytes = pgnBytes,
                    sourcePgnSha256 = pgnSha256,
                    fetchedGames = games,
                    parsedGames = gameResult.Parsed,
                    novelGames = gameResult.Novel,
                    newlyAppliedGames = gameResult.Applied,
                    repairedGames = gameResult.Repaired,
                    persistedVerifiedGames = gameResult.Verified,
                    persistedVerifiedPlies = gameResult.VerifiedPlies,
                    exactPersistedReadback = gameResult.Verified == games,
                    profiles = profileResult.Profiles,
                    identityLinks = profileResult.Links,
                }, new JsonSerializerOptions { WriteIndented = true }), ct);
                lab.AddArtifact(slot, "recording-proof.json", proofPath);
                lab.Publish(slot, new ChessLabMetricEvent("source_pgn_bytes", pgnBytes));
                lab.Publish(slot, new ChessLabLogEvent("info",
                    $"recording proof: {gameResult.Verified} games / {gameResult.VerifiedPlies} plies exact-readback verified · "
                    + $"PGN sha256 {pgnSha256}"));

                lab.UpdateSummary(slot, new ChessLabJobSummary(
                    gameResult.Verified, games,
                    $"{profileResult.Profiles} profiles · {profileResult.Links} identity links · "
                    + $"{games} provider games fetched and exact-readback verified · "
                    + $"{gameResult.Applied} new · {gameResult.Repaired} repaired"));
            }
            else
            {
                lab.UpdateSummary(slot, new ChessLabJobSummary(
                    games, games, $"{profiles.Count} profiles acquired · {games} fetched oldest-to-newest · not ingested"));
            }

            if (persistPgn)
                lab.AddArtifact(slot, "games.pgn", outPath);
            journal?.Complete();
            completed = true;
        }
        catch (OperationCanceledException)
        {
            journal?.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            journal?.Fail(ex);
            // Keep the exact downloaded input reachable from the failed job, including
            // the archive that did not finish admission. Successful prior commits remain.
            if (outPath is not null && File.Exists(outPath))
            {
                if (!persistPgn)
                    outPath = ChessLabStorage.PersistArtifact(outPath, LabDir, slot.Job.Id, "games.pgn");
                lab.AddArtifact(slot, "games.pgn", outPath);
                lab.Publish(slot, new ChessLabLogEvent("info", "Downloaded PGN retained as games.pgn on this failed job."));
            }
            throw;
        }
        finally
        {
            if (ingestor is not null) await ingestor.DisposeAsync();
            if (!persistPgn && completed && outPath is not null)
                ChessLabStorage.DeleteFile(outPath);
            if (spoolDir is not null)
                ChessLabStorage.DeleteDirectoryIfEmpty(spoolDir);
        }
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    public static async Task RunPlayerProfileAsync(
        ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string user = Config(slot.Job.Config, "user", "").Trim();
        string site = Config(slot.Job.Config, "site", "lichess");
        string fideId = Config(slot.Job.Config, "fideId", "").Trim();
        bool ingest = bool.TryParse(Config(slot.Job.Config, "ingest", "true"), out bool ingestValue) && ingestValue;
        if (user.Length == 0) throw new ArgumentException("A provider username is required.");

        var online = await ChessGameFetcher.FetchProfileAsync(user, site, ct);
        var profiles = new List<ChessPlayerProfile> { online };
        if (fideId.Length > 0)
            profiles.Add(await ChessGameFetcher.FetchProfileAsync(fideId, "fide", ct));

        lab.Publish(slot, ProfileTable(profiles));
        await AddProfileArtifactsAsync(lab, slot, profiles, ct);

        if (!ingest)
        {
            lab.UpdateSummary(slot, new ChessLabJobSummary(profiles.Count, profiles.Count,
                $"{profiles.Count} profiles acquired · not ingested"));
            Finish(lab, slot, ChessLabJobState.Completed);
            return;
        }

        var liveHost = await lab.GetLiveHostAsync(ct);
        // A profile import is an ingest run like every other Lab import: one ingest_run_journal
        // row and exactly one terminal ok|failed|cancelled status (with its error).
        var journal = await ChessLabIngestRun.StartAsync(liveHost.DataSource, lab.Log, slot.Job.Id,
            $"{ChessGameFetcher.Sanitize(site.Trim().ToLowerInvariant())}/{ChessGameFetcher.Sanitize(online.ProviderId)}/profile", ct);
        lab.Publish(slot, new ChessLabLogEvent("info",
            $"ingest run {journal.RunId} ({ChessLabIngestRun.SourceName}) journaled"));
        ChessPgnIngestor.ProfileResult result;
        try
        {
            await using var ingestor = await ChessPgnIngestor.AttachAsync(liveHost, ct);
            journal.Attach(ingestor);
            result = await ingestor.IngestPlayerProfilesAsync(profiles, ct);
            journal.Complete();
        }
        catch (OperationCanceledException)
        {
            journal.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            journal.Fail(ex);
            throw;
        }
        lab.Publish(slot, new ChessLabMetricEvent("profiles_ingested", result.Profiles));
        lab.Publish(slot, new ChessLabMetricEvent("identity_links", result.Links));
        lab.UpdateSummary(slot, new ChessLabJobSummary(result.Profiles, profiles.Count,
            $"{result.Profiles} profiles · {result.Links} identity links"));
        Finish(lab, slot, ChessLabJobState.Completed);
    }

    private static ChessLabTableEvent ProfileTable(IReadOnlyList<ChessPlayerProfile> profiles)
        => new("Acquired player profiles",
            ["Provider", "Provider ID", "Display name", "Real name", "Title", "Federation", "Ratings", "Avatar"],
            profiles.Select(static p => (IReadOnlyList<string>)[
                p.Provider, p.ProviderId, p.DisplayName, p.RealName ?? "", p.Title ?? "",
                p.Federation ?? "", string.Join(", ", p.Ratings.Select(static x => $"{x.Key} {x.Value}")),
                p.AvatarUrl ?? "",
            ]).ToArray());

    private static async Task AddProfileArtifactsAsync(
        ChessLabService lab, ChessLabService.JobSlot slot,
        IReadOnlyList<ChessPlayerProfile> profiles, CancellationToken ct)
    {
        string directory = Path.Combine(LabDir, slot.Job.Id);
        Directory.CreateDirectory(directory);
        foreach (var profile in profiles)
        {
            string name = $"profile-{ChessGameFetcher.Sanitize(profile.Provider)}.json";
            string path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), ct);
            lab.AddArtifact(slot, name, path);
        }
    }

    private static void AddPgnArtifact(
        ChessLabService lab,
        ChessLabService.JobSlot slot,
        IEnumerable<MatchPgnGame> games,
        string eventName)
    {
        string workDir = Path.Combine(LabDir, slot.Job.Id);
        Directory.CreateDirectory(workDir);
        string pgnPath = Path.Combine(workDir, "games.pgn");
        ChessPgnWriter.WriteFile(pgnPath, games, @event: eventName);
        lab.AddArtifact(slot, "games.pgn", pgnPath);
    }

    private static void Finish(ChessLabService lab, ChessLabService.JobSlot slot, ChessLabJobState state, string? msg = null)
    {
        lock (slot.Gate)
        {
            slot.Job = slot.Job with
            {
                State = state,
                FinishedAt = DateTimeOffset.UtcNow,
                Summary = slot.Job.Summary with { Message = msg ?? slot.Job.Summary.Message },
            };
        }
        lab.Publish(slot, new ChessLabDoneEvent(state, msg));
        slot.Channel.Writer.TryComplete();
        slot.Terminal.Complete();
    }

    private static string Config(IReadOnlyDictionary<string, string> cfg, string key, string def)
        => cfg.TryGetValue(key, out var v) ? v : def;

    internal static bool IsReviewablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return false; }
        if (!full.EndsWith(".pgn", StringComparison.OrdinalIgnoreCase)) return false;
        string? gamesDir = null;
        try { gamesDir = LaplaceInstall.ResolveChessGamesDir(); }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException) { }
        foreach (var root in new[] { LabDir, gamesDir })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var rooted = Path.GetFullPath(root);
            if (full.StartsWith(rooted.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static Action<int, int, string, string> LiveBoardPublisher(
        ChessLabService lab, ChessLabService.JobSlot slot, string nameA, string nameB)
    {
        var lastEmit = new ConcurrentDictionary<int, long>();
        return (game, ply, uci, toKey) =>
        {
            long now = Environment.TickCount64;
            if (ply > 1 && lastEmit.TryGetValue(game, out var last) && now - last < 250)
                return;
            lastEmit[game] = now;
            if (!Laplace.Modality.Chess.PositionContent.TryFenFromSurface(toKey, out var fen))
                return;
            bool aWhite = game % 2 == 0;
            lab.Publish(slot, new ChessLabBoardEvent(
                game, ply, uci, fen, aWhite ? nameA : nameB, aWhite ? nameB : nameA));
        };
    }

    private static int ResolveConcurrency(IReadOnlyDictionary<string, string> cfg, string key = "concurrency")
    {
        if (cfg.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed))
            return parsed <= 0 ? CpuTopology.PerformanceLogicalProcessorCount : parsed;

        return CpuTopology.PerformanceLogicalProcessorCount;
    }

    private static string Short(string name) => string.IsNullOrEmpty(name) ? "?" : (name.Length > 16 ? name[..16] : name);

    private sealed class LabLogger(ChessLabService lab, ChessLabService.JobSlot slot) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => lab.Publish(slot, new ChessLabLogEvent(logLevel.ToString().ToLowerInvariant(), formatter(state, exception)));
    }
}
