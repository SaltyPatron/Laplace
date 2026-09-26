using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

/// <summary>
/// `laplace evict &lt;sourceName&gt; [--relations A,B] [--marker-types X,Y] [--rederive]`
/// — retraction of one source's testimony. Eviction is the inverse of the consensus fold
/// and runs in the evict_source extension procedure over the consensus_fold aggregate;
/// this verb resolves names to content-addressed ids, calls it, and with --rederive re-runs
/// the lane. With the lane's Version bumped first, re-derivation counts each witness once.
/// </summary>
internal static class EvictCommands
{
    /// <summary>
    /// Calculated lanes that can be re-derived: source name → the `laplace ingest` key that
    /// re-runs it, plus its derivation-marker entity type (deleted by evict_source so the
    /// lane re-yields every unit). Any source can be evicted by name; only these support
    /// --rederive and get marker cleanup by default.
    /// </summary>
    private static readonly Dictionary<string, (string IngestKey, string[] MarkerTypes)> KnownLanes =
        new(StringComparer.Ordinal)
        {
            ["ChessAnalysis"]   = ("chess-analyze",    ["Chess_AnalysisMarker"]),
            ["ChessTrajectory"] = ("chess-trajectory", ["Chess_AnalysisMarker"]),
            ["ChessStockfish"]  = ("chess-eval",       ["Chess_AnalysisMarker"]),
            ["ChessTransitions"] = ("chess-transitions", ["Chess_AnalysisMarker"]),
        };

    public static async Task<int> EvictAsync(string[] args)
    {
        string? sourceName = null;
        string[]? relationNames = null;
        string[]? markerTypeNames = null;
        bool rederive = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--relations" when i + 1 < args.Length:
                    relationNames = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--marker-types" when i + 1 < args.Length:
                    markerTypeNames = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--rederive":
                    rederive = true;
                    break;
                default:
                    if (sourceName is null && !args[i].StartsWith('-')) { sourceName = args[i]; break; }
                    return Fail($"evict: unrecognized argument '{args[i]}'");
            }
        }
        if (sourceName is null)
            return Fail("usage: laplace evict <sourceName> [--relations A,B] [--marker-types X,Y] [--rederive]");

        KnownLanes.TryGetValue(sourceName, out var lane);
        markerTypeNames ??= lane.MarkerTypes;
        if (rederive && lane.IngestKey is null)
            return Fail($"evict: --rederive knows no ingest lane for source '{sourceName}' "
                + $"(known: {string.Join(", ", KnownLanes.Keys)})");

        // Ids resolve through the native hash, the same derivation the SQL helpers
        // source_id()/relation_type_id()/entity_type_id() run.
        var sourceId = SubstrateCanonicalIds.Source(sourceName);
        Hash128[]? relationIds = relationNames?.Select(Hash128.OfCanonical).ToArray();
        Hash128[]? markerTypeIds = markerTypeNames?.Select(Hash128.OfCanonical).ToArray();

        Console.WriteLine(
            $"evicting testimony of {sourceName} "
            + (relationNames is null ? "(all relations discovered from evidence)" : $"({relationNames.Length} relation(s))")
            + (markerTypeNames is null ? ", no marker cleanup" : $", markers: {string.Join(", ", markerTypeNames)}")
            + " ...");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Eviction and its receipt go through NpgsqlSubstrateReader, shared by every interface.
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        var reader = new NpgsqlSubstrateReader(ds);
        await reader.EvictSourceAsync(sourceId, relationIds, markerTypeIds);

        // Receipt: evidence rows still under the source (zero unless --relations restricted it).
        long remaining = await reader.CountEvidenceBySourceAsync(sourceId);
        sw.Stop();
        Console.WriteLine(
            $"evict {sourceName} complete in {sw.Elapsed.TotalSeconds:F1}s — "
            + $"{remaining} evidence row(s) remaining under the source"
            + (remaining == 0 ? "" : " (restricted --relations leaves other relations in place)"));

        if (!rederive) return 0;

        Console.WriteLine($"re-deriving via `laplace ingest {lane.IngestKey}` ...");
        return await IngestCommands.IngestAsync([lane.IngestKey]);
    }
}
