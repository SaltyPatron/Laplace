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
            candidates = [await FideRatingList.FindByIdAsync(query, ct)];
        }
        else
        {
            candidates = await FideRatingList.SearchAsync(query, limit, ct);
        }

        if (candidates.Count == 0)
            throw new InvalidDataException(
                $"FIDE published rating list returned no valid candidates for '{query}'.");

        lab.Publish(slot, FideTable($"FIDE matches for {query}", candidates, actionable: true));
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

        lab.Publish(slot, FideTable($"FIDE {cohort} top {candidates.Count}", candidates, actionable: true));
        if (!ingest)
        {
            lab.UpdateSummary(slot, new ChessLabJobSummary(
                candidates.Count, candidates.Count,
                $"{candidates.Count} official FIDE profiles selected · not ingested"));
            return;
        }

        // The published list is already the authoritative FIDE profile estate. HTML
        // profile pages are optional enrichment and cannot make admission all-or-nothing.
        var profiles = candidates.Select(candidate => Profile(candidate, cohort)).ToArray();
        var liveHost = await lab.GetLiveHostAsync(ct);
        await using var ingestor = await ChessPgnIngestor.AttachAsync(liveHost, ct);
        var result = await ingestor.IngestPlayerProfilesAsync(profiles, ct);
        lab.Publish(slot, new ChessLabMetricEvent("profiles_ingested", result.Profiles));
        lab.UpdateSummary(slot, new ChessLabJobSummary(
            result.Profiles, candidates.Count,
            $"{result.Profiles} official FIDE profiles ingested from {cohort}"));
    }

    public static async Task RunProfileAsync(
        ChessLabService lab, ChessLabService.JobSlot slot, CancellationToken ct)
    {
        string fideId = Config(slot.Job.Config, "fideId", "").Trim();
        var candidate = await FideRatingList.FindByIdAsync(fideId, ct);
        var profile = Profile(candidate, cohort: null);
        var liveHost = await lab.GetLiveHostAsync(ct);
        await using var ingestor = await ChessPgnIngestor.AttachAsync(liveHost, ct);
        var result = await ingestor.IngestPlayerProfilesAsync([profile], ct);
        lab.Publish(slot, ProfileTable(profile));
        lab.Publish(slot, new ChessLabMetricEvent("profiles_ingested", result.Profiles));
        lab.UpdateSummary(slot, new ChessLabJobSummary(
            result.Profiles, 1, $"{profile.DisplayName} imported from official FIDE list"));
    }

    internal static ChessPlayerProfile Profile(FidePlayerCandidate candidate, string? cohort)
    {
        var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (candidate.Standard > 0) ratings["standard"] = candidate.Standard;
        if (candidate.Rapid > 0) ratings["rapid"] = candidate.Rapid;
        if (candidate.Blitz > 0) ratings["blitz"] = candidate.Blitz;
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (candidate.BirthYear > 0) facts["birth_year"] = candidate.BirthYear.ToString();
        if (!string.IsNullOrWhiteSpace(cohort)) facts["cohort"] = cohort;
        if (candidate.Rank is { } rank) facts["rank"] = rank.ToString();
        return new ChessPlayerProfile(
            "fide", candidate.FideId, candidate.Name, candidate.Name, null,
            candidate.Title, candidate.Federation, candidate.FideId, null,
            [candidate.Name], [$"https://ratings.fide.com/profile/{candidate.FideId}"],
            ratings, facts);
    }

    private static ChessLabTableEvent ProfileTable(ChessPlayerProfile profile)
        => new("Imported FIDE profile",
            ["FIDE ID", "Name", "Title", "Federation", "Ratings"],
            [[profile.ProviderId, profile.DisplayName, profile.Title ?? "", profile.Federation ?? "",
              string.Join(", ", profile.Ratings.Select(static x => $"{x.Key} {x.Value}"))]]);

    private static ChessLabTableEvent FideTable(
        string title, IReadOnlyList<FidePlayerCandidate> candidates, bool actionable)
        => new(title,
            ["Rank", "FIDE ID", "Name", "Title", "Fed", "Standard", "Rapid", "Blitz", "Born"],
            candidates.Select(static c => (IReadOnlyList<string>)[
                c.Rank?.ToString() ?? "", c.FideId, c.Name, c.Title ?? "", c.Federation,
                c.Standard == 0 ? "" : c.Standard.ToString(),
                c.Rapid == 0 ? "" : c.Rapid.ToString(),
                c.Blitz == 0 ? "" : c.Blitz.ToString(),
                c.BirthYear == 0 ? "" : c.BirthYear.ToString(),
            ]).ToArray(),
            actionable ? new ChessLabTableAction("FIDE · Import profile", "fide-profile", "fideId", 1) : null);

    private static string Config(IReadOnlyDictionary<string, string> cfg, string key, string fallback)
        => cfg.TryGetValue(key, out var value) ? value : fallback;
}
