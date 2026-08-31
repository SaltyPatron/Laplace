using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

/// <summary>
/// `laplace evict &lt;sourceName&gt; [--relations A,B] [--marker-types X,Y] [--rederive]`
/// — lawful retraction of one source's testimony (GH #508). ORCHESTRATION ONLY:
/// eviction is the inverse operator of the fold and runs where the fold's math lives
/// (the evict_source extension PROCEDURE over the consensus_fold aggregate); this verb
/// resolves names to content-addressed ids, CALLs the procedure, and optionally
/// re-runs the lane. Bump the lane's Version first, then `evict --rederive`, and the
/// hydrator re-derives every unit without double-counting a single witness.
/// </summary>
internal static class EvictCommands
{
    /// <summary>
    /// The calculated lanes this verb knows how to re-derive: source name → the
    /// `laplace ingest` key that re-runs the lane, plus the lane's derivation-marker
    /// entity type (the gate evict_source deletes so the hydrator re-yields every
    /// unit). Any source can be evicted by name; only listed lanes support
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

        // Ids resolve through the system's native hash — the same derivations the SQL
        // helpers source_id()/relation_type_id()/entity_type_id() run.
        var sourceId = SubstrateCanonicalIds.Source(sourceName);
        Hash128[]? relationIds = relationNames?.Select(Hash128.OfCanonical).ToArray();
        Hash128[]? markerTypeIds = markerTypeNames?.Select(Hash128.OfCanonical).ToArray();

        Console.WriteLine(
            $"evicting testimony of {sourceName} "
            + (relationNames is null ? "(all relations discovered from evidence)" : $"({relationNames.Length} relation(s))")
            + (markerTypeNames is null ? ", no marker cleanup" : $", markers: {string.Join(", ", markerTypeNames)}")
            + " ...");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // The eviction call and its receipt live on the shared read surface
        // (NpgsqlSubstrateReader), so this verb stays orchestration and every caller
        // gets one implementation of the fact.
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        var reader = new NpgsqlSubstrateReader(ds);
        await reader.EvictSourceAsync(sourceId, relationIds, markerTypeIds);

        // The receipt: zero surviving evidence rows under the source.
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
