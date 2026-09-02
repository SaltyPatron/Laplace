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
        IReadOnlyList<FidePlayerCandidate> candidates;
        if (query.Length is >= 4 and <= 12 && query.All(char.IsDigit))
        {
            // An exact provider id is already a selected FIDE identity, not fuzzy
            // discovery. Preserve the historical exact-ID contract and enrich that
            // exact provider coordinate directly.
            var profile = await ChessGameFetcher.FetchFideProfileAsync(query, ct);
            candidates = [Candidate(profile)];
        }
        else
        {
            candidates = await FideRatingList.SearchAsync(query, limit, ct);
        }

        if (candidates.Count == 0)
            throw new InvalidDataException(
                $"FIDE published rating list returned no valid candidates for '{query}'.");

        lab.Publish(slot, FideTable($"FIDE matches for {query}", candidates));
        lab.Publish(slot, new ChessLabMetricEvent("matches", candidates.Count));
        lab.UpdateSummary(slot, new ChessLabJobSummary(
            candidates.Count, candidates.Count,
            query.All(char.IsDigit)
                ? $"{candidates.Count} exact official FIDE identity"
                : $"{candidates.Count} official FIDE candidates from published rating list"));
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

    private static FidePlayerCandidate Candidate(ChessPlayerProfile profile)
    {
        int birthYear = profile.Facts.TryGetValue("birth_year", out var born)
            && int.TryParse(born, out int year) ? year : 0;
        return new FidePlayerCandidate(
            profile.ProviderId,
            profile.DisplayName,
            profile.Title,
            profile.Federation ?? "",
            profile.Ratings.TryGetValue("standard", out int standard) ? standard : 0,
            profile.Ratings.TryGetValue("rapid", out int rapid) ? rapid : 0,
            profile.Ratings.TryGetValue("blitz", out int blitz) ? blitz : 0,
            birthYear,
            null);
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
