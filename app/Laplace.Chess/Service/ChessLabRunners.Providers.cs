using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
        int maxConcurrent = ParseInt(slot.Job.Config, "maxConcurrent", LichessDefaults.MaxConcurrent, 1, 64);
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
        bool all = ParseBool(slot.Job.Config, "all", true);
        bool ingest = ParseBool(slot.Job.Config, "ingest", true);
        bool persistPgn = ChessLabStorage.PersistPgn(slot.Job.Config, defaultValue: !ingest);
        int? max = ChessGameFetcher.ResolveArchiveLimit(all, Config(slot.Job.Config, "max", ""));
        string fideId = Config(slot.Job.Config, "fideId", "").Trim();
        if (user.Length == 0) throw new ArgumentException("A provider username is required.");
        string provider = ChessFideLinkStore.ValidateProvider(site, allowAliases: true);
        if (provider == "fide")
            throw new ArgumentException("site must be 'chesscom' or 'lichess'.", nameof(site));
        if (fideId.Length > 0)
            fideId = ChessFideLinkStore.ValidateFideId(fideId);

        var online = await ChessGameFetcher.FetchProfileAsync(user, provider, ct);
        var profiles = new List<ChessPlayerProfile> { online };
        if (fideId.Length > 0)
            profiles.Add(await ChessGameFetcher.FetchProfileAsync(fideId, "fide", ct));
        lab.Publish(slot, ProfileTable(profiles));
        await AddProfileArtifactsAsync(lab, slot, profiles, ct);

        string? spoolDir = null;
        string? outPath = null;
        bool completed = false;
        ChessLabIngestRun? journal = null;
        try
        {
            string fileName = $"{ChessGameFetcher.Sanitize(online.ProviderId)}_{ChessGameFetcher.Sanitize(provider)}.pgn";
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

            int games = await ChessGameFetcher.FetchAsync(online.ProviderId, provider, max, 0, outPath,
                msg => lab.Publish(slot, new ChessLabLogEvent("info", msg)), ct, null);
            lab.Publish(slot, new ChessLabMetricEvent("games_fetched", games));

            if (ingest)
            {
                string witness = provider == "chesscom" ? "Chess.com" : "Lichess";
                var liveHost = await lab.GetLiveHostAsync(ct);
                journal = await ChessLabIngestRun.StartAsync(liveHost, lab.Log, slot.Job.Id,
                    $"{ChessGameFetcher.Sanitize(provider)}/{ChessGameFetcher.Sanitize(online.ProviderId)}",
                    witness, ct, trustClass: ChessGameRecorder.CuratedCorpusTrustClass);
                lab.Publish(slot, new ChessLabLogEvent("info",
                    $"ingest run {journal.RunId} ({journal.SourceName} @ {ChessGameRecorder.CuratedCorpusTrustClass}) journaled"));
                await journal.IngestUnitAsync(fileName, outPath, ct);
                lab.Publish(slot, new ChessLabMetricEvent("games_ingested", games));

                bool linked = false;
                if (fideId.Length > 0)
                {
                    var link = await ChessFideLinkStore.UpsertAsync(
                        fideId, provider, online.ProviderId, ct: ct);
                    await liveHost.IngestSourceFileAsync(
                        ChessGameRecorder.CuratedFideLinksSource,
                        link.FilePath,
                        ct);
                    linked = true;
                    lab.Publish(slot, new ChessLabMetricEvent("identity_linked", 1));
                    lab.Publish(slot, new ChessLabLogEvent("info",
                        $"linked FIDE {link.FideId} <-> {link.Provider}:{link.Username} admitted via source {ChessGameRecorder.CuratedFideLinksSource} ({ChessGameRecorder.CuratedFideLinkWitness} @ {ChessGameRecorder.CuratedFideLinkTrustClass})"));
                }

                lab.UpdateSummary(slot, new ChessLabJobSummary(
                    games, games,
                    linked
                        ? $"{profiles.Count} profiles acquired · {games} {witness} games witnessed · FIDE {fideId} linked"
                        : $"{profiles.Count} profiles acquired · {games} {witness} games witnessed"));
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
            // Keep the exact downloaded input reachable from the failed job.
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
        bool ingest = ParseBool(slot.Job.Config, "ingest", true);
        if (user.Length == 0) throw new ArgumentException("A provider username is required.");
        string provider = ChessFideLinkStore.ValidateProvider(site, allowAliases: true);
        if (provider == "fide")
            throw new ArgumentException("site must be 'chesscom' or 'lichess'.", nameof(site));
        if (fideId.Length > 0)
            fideId = ChessFideLinkStore.ValidateFideId(fideId);

        var online = await ChessGameFetcher.FetchProfileAsync(user, provider, ct);
        var profiles = new List<ChessPlayerProfile> { online };
        if (fideId.Length > 0)
            profiles.Add(await ChessGameFetcher.FetchProfileAsync(fideId, "fide", ct));

        lab.Publish(slot, ProfileTable(profiles));
        await AddProfileArtifactsAsync(lab, slot, profiles, ct);

        if (ingest && fideId.Length > 0)
        {
            var link = await ChessFideLinkStore.UpsertAsync(
                fideId, provider, online.ProviderId, ct: ct);
            var liveHost = await lab.GetLiveHostAsync(ct);
            await liveHost.IngestSourceFileAsync(
                ChessGameRecorder.CuratedFideLinksSource,
                link.FilePath,
                ct);
            lab.Publish(slot, new ChessLabMetricEvent("identity_linked", 1));
            lab.Publish(slot, new ChessLabLogEvent("info",
                $"linked FIDE {link.FideId} <-> {link.Provider}:{link.Username} admitted via source {ChessGameRecorder.CuratedFideLinksSource} ({ChessGameRecorder.CuratedFideLinkWitness} @ {ChessGameRecorder.CuratedFideLinkTrustClass})"));
            lab.UpdateSummary(slot, new ChessLabJobSummary(profiles.Count, profiles.Count,
                $"{profiles.Count} profiles acquired · FIDE {link.FideId} linked ({ChessGameRecorder.CuratedFideLinkTrustClass})"));
        }
        else
        {
            lab.UpdateSummary(slot, new ChessLabJobSummary(profiles.Count, profiles.Count,
                $"{profiles.Count} profiles acquired"));
        }
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
        => lab.Finish(slot, state, msg);

    private static string Config(IReadOnlyDictionary<string, string> cfg, string key, string def)
        => cfg.TryGetValue(key, out var v) ? v : def;

    public static void ValidateConfig(ChessLabJobKind kind, IReadOnlyDictionary<string, string>? config)
    {
        if (config is null || config.Count == 0) return;

        switch (kind)
        {
            case ChessLabJobKind.SubstrateTest:
                _ = ParseInt(config, "games", 20, 1, 1000);
                _ = ParseInt(config, "depth", 4, 1, 20);
                _ = ParseInt(config, "maxPlies", 160, 1, 1000);
                _ = ResolveConcurrency(config);
                _ = ParseBool(config, "openings", false);
                break;
            case ChessLabJobKind.Ladder:
                _ = ParseInt(config, "games", 20, 1, 1000);
                _ = ParseInt(config, "depth", 4, 1, 20);
                _ = ParseInt(config, "maxPlies", 160, 1, 1000);
                _ = ResolveConcurrency(config);
                _ = ParseBool(config, "record", false);
                break;
            case ChessLabJobKind.Tactics:
                _ = ParseInt(config, "depth", 6, 1, 20);
                break;
            case ChessLabJobKind.Review:
                _ = ParseInt(config, "depth", 4, 1, 20);
                _ = ParseInt(config, "maxGames", 10, 1, 1000);
                break;
            case ChessLabJobKind.Cutechess:
                _ = ParseInt(config, "rounds", 10, 1, 1000);
                _ = ParseInt(config, "depth", 0, 0, 20);
                _ = ParseDouble(config, "st", 1.0, 0.05, 3600.0);
                _ = ParseInt(config, "elo", 2000, 100, 4000);
                _ = ParseBool(config, "limitStrength", true);
                _ = ParseInt(config, "concurrency", 1, 1, 256);
                _ = ParseBool(config, "ingest", true);
                break;
            case ChessLabJobKind.LichessBot:
                _ = ParseInt(config, "maxConcurrent", LichessDefaults.MaxConcurrent, 1, 64);
                break;
            case ChessLabJobKind.LichessFetch:
                _ = ParseBool(config, "all", true);
                _ = ParseBool(config, "ingest", true);
                if (config.TryGetValue("fideId", out var fetchFide) && !string.IsNullOrWhiteSpace(fetchFide))
                    _ = ChessFideLinkStore.ValidateFideId(fetchFide);
                break;
            case ChessLabJobKind.PlayerProfile:
                _ = ParseBool(config, "ingest", true);
                if (config.TryGetValue("fideId", out var profileFide) && !string.IsNullOrWhiteSpace(profileFide))
                    _ = ChessFideLinkStore.ValidateFideId(profileFide);
                break;
            case ChessLabJobKind.FideSearch:
            case ChessLabJobKind.FideRoster:
                _ = ParseInt(config, "limit", 25, 1, 100);
                break;
        }
    }

    internal static int ParseInt(IReadOnlyDictionary<string, string> cfg, string key, int def, int min, int max)
    {
        if (!cfg.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return def;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            throw new ArgumentException($"Invalid '{key}' value '{raw}'; expected an integer in [{min}, {max}].", key);
        if (parsed < min || parsed > max)
            throw new ArgumentException($"'{key}' value {parsed} must be between {min} and {max}.", key);
        return parsed;
    }

    internal static double ParseDouble(IReadOnlyDictionary<string, string> cfg, string key, double def, double min, double max)
    {
        if (!cfg.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return def;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
            throw new ArgumentException($"Invalid '{key}' value '{raw}'; expected a finite number in [{min}, {max}].", key);
        if (parsed < min || parsed > max)
            throw new ArgumentException($"'{key}' value {parsed} must be between {min} and {max}.", key);
        return parsed;
    }

    internal static bool ParseBool(IReadOnlyDictionary<string, string> cfg, string key, bool def)
    {
        if (!cfg.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return def;
        if (!bool.TryParse(raw.Trim(), out bool parsed))
            throw new ArgumentException($"Invalid '{key}' value '{raw}'; expected true or false.", key);
        return parsed;
    }

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
        int cap = Math.Max(1, Environment.ProcessorCount / 2);
        int defaultConcurrency = Math.Clamp(CpuTopology.PerformanceLogicalProcessorCount, 1, cap);
        if (!cfg.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return defaultConcurrency;

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
            throw new ArgumentException($"Invalid '{key}' value '{raw}'; expected non-negative integer.", key);

        return parsed == 0 ? defaultConcurrency : Math.Clamp(parsed, 1, cap);
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
