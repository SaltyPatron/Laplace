using System.Collections.Concurrent;

namespace Laplace.Chess.Service;

/// <summary>
/// FIDE acquisition for the operator Lab. Discovery comes from FIDE's published
/// rating estate; profile pages enrich identities only after a FIDE id has been
/// selected. A provider request that acquires nothing is a failed operation, not a
/// successful zero-row ingest.
/// </summary>
internal static class FideLabRunners
{
    public static async Task RunSearchAsync(
        ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string query = Config(slot.Job.Config, "query", "").Trim();
        if (query.Length < 2)
            throw new ArgumentException("FIDE search needs at least two characters.", nameof(query));

        int limit = Math.Clamp(int.Parse(Config(slot.Job.Config, "limit", "25")), 1, 100);
        var candidates = await FideRatingList.SearchAsync(query, limit, ct);
        if (candidates.Count == 0)
            throw new InvalidDataException(
                $"FIDE published rating list returned no valid candidates for '{query}'.");

        lab.Publish(slot, FideTable($"FIDE matches for {query}", candidates));
        lab.Publish(slot, new ChessLabMetricEvent("matches", candidates.Count));
        lab.UpdateSummary(slot, new ChessLabJobSummary(
            candidates.Count, candidates.Count,
            $"{candidates.Count} official FIDE candidates from published rating list"));
    }

    public static async Task RunRosterAsync(
        ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string cohort = Config(slot.Job.Config, "cohort", "open").Trim().ToLowerInvariant();
        int limit = Math.Clamp(int.Parse(Config(slot.Job.Config, "limit", "25")), 1, 100);
        bool ingest = Config(slot.Job.Config, "ingest", "true") == "true";

        if (!ChessGameFetcher.FideCohorts.Contains(cohort))
            throw new ArgumentException($"unknown FIDE cohort '{cohort}'", nameof(cohort));

        var candidates = await FideRatingList.TopAsync(cohort, limit, ct);
        if (candidates.Count == 0)
            throw new InvalidDataException(
                $"FIDE published rating list returned no valid players for cohort '{cohort}'.");

        lab.Publish(slot, FideTable($"FIDE {cohort} top {candidates.Count}", candidates));
        if (!ingest)
        {
            lab.UpdateSummary(slot, new ChessLabJobSummary(
                candidates.Count, candidates.Count,
                $"{candidates.Count} official FIDE profiles selected · not ingested"));
            return;
        }

        // Discovery establishes provider ids and ranking facts. The selected profile
        // pages then enrich those exact provider identities; names alone never assert
        // cross-provider identity.
        var fetched = new ConcurrentDictionary<string, ChessPlayerProfile>(StringComparer.Ordinal);
        int done = 0;
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (candidate, token) =>
            {
                var profile = await ChessGameFetcher.FetchFideProfileAsync(candidate.FideId, token);
                var facts = profile.Facts.ToDictionary(
                    static x => x.Key, static x => x.Value, StringComparer.OrdinalIgnoreCase);
                facts["cohort"] = cohort;
                facts["rank"] = candidate.Rank?.ToString() ?? "";
                facts["rating_list_standard"] = candidate.Standard.ToString();
                facts["rating_list_rapid"] = candidate.Rapid.ToString();
                facts["rating_list_blitz"] = candidate.Blitz.ToString();
                fetched[candidate.FideId] = profile with { Facts = facts };

                int current = Interlocked.Increment(ref done);
                lab.UpdateSummary(slot, new ChessLabJobSummary(
                    current, candidates.Count, $"profiles {current}/{candidates.Count}"));
                lab.Publish(slot, new ChessLabProgressEvent(
                    current, candidates.Count, candidate.Name));
            });

        var profiles = candidates.Select(candidate => fetched[candidate.FideId]).ToArray();
        var liveHost = await lab.GetLiveHostAsync(ct);
        await using var ingestor = await ChessPgnIngestor.AttachAsync(liveHost, ct);
        var result = await ingestor.IngestPlayerProfilesAsync(profiles, ct);
        lab.Publish(slot, new ChessLabMetricEvent("profiles_ingested", result.Profiles));
        lab.UpdateSummary(slot, new ChessLabJobSummary(
            result.Profiles, candidates.Count,
            $"{result.Profiles} official FIDE profiles ingested from {cohort}"));
    }

    private static ChessLabTableEvent FideTable(
        string title, IReadOnlyList<FidePlayerCandidate> candidates)
        => new(title,
            ["Rank", "FIDE ID", "Name", "Title", "Fed", "Standard", "Rapid", "Blitz", "Born"],
            candidates.Select(static c => (IReadOnlyList<string>)[
                c.Rank?.ToString() ?? "", c.FideId, c.Name, c.Title ?? "", c.Federation,
                c.Standard == 0 ? "" : c.Standard.ToString(),
                c.Rapid == 0 ? "" : c.Rapid.ToString(),
                c.Blitz == 0 ? "" : c.Blitz.ToString(),
                c.BirthYear == 0 ? "" : c.BirthYear.ToString(),
            ]).ToArray());

    private static string Config(IReadOnlyDictionary<string, string> cfg, string key, string fallback)
        => cfg.TryGetValue(key, out var value) ? value : fallback;
}
