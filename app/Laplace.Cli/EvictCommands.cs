using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD.Npgsql;
using static Laplace.Cli.CliRuntime;

namespace Laplace.Cli;

/// <summary>
/// `laplace evict &lt;sourceName&gt; [--relations A,B] [--rederive]`
/// — retraction of one source's testimony. Eviction is the inverse of the consensus fold
/// and runs in the evict_source extension procedure over the consensus_fold aggregate;
/// this verb resolves names to content-addressed ids, calls it, and with --rederive re-runs
/// the lane. With the lane's Version bumped first, re-derivation counts each witness once.
/// </summary>
internal static class EvictCommands
{
    /// <summary>
    /// Calculated lanes that can be re-derived: source name → the `laplace ingest` key that
    /// re-runs it. Witness completion receipts are retracted; canonical content
    /// remains shared. Any source can be evicted; these also support --rederive.
    /// </summary>
    private static readonly Dictionary<string, string> KnownLanes =
        new(StringComparer.Ordinal)
        {
            ["ChessAnalysis"] = "chess-analyze",
            ["ChessTrajectory"] = "chess-trajectory",
            ["ChessStockfish"] = "chess-eval",
            ["ChessTransitions"] = "chess-transitions",
        };

    public static async Task<int> EvictAsync(string[] args)
    {
        string? sourceName = null;
        string[]? relationNames = null;
        bool rederive = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--relations" when i + 1 < args.Length:
                    relationNames = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--marker-types":
                    return Fail("evict: --marker-types is retired; eviction clears witness completion receipts and preserves canonical content.");
                case "--rederive":
                    rederive = true;
                    break;
                default:
                    if (sourceName is null && !args[i].StartsWith('-')) { sourceName = args[i]; break; }
                    return Fail($"evict: unrecognized argument '{args[i]}'");
            }
        }
        if (sourceName is null)
            return Fail("usage: laplace-mono evict <sourceName> [--relations A,B] [--rederive]");
        if (relationNames is { Length: 0 })
            return Fail("evict: --relations requires at least one relation name.");

        KnownLanes.TryGetValue(sourceName, out var ingestKey);
        if (rederive && ingestKey is null)
            return Fail($"evict: --rederive knows no ingest lane for source '{sourceName}' "
                + $"(known: {string.Join(", ", KnownLanes.Keys)})");

        // Ids resolve through the native hash, the same derivation the SQL helpers
        // source_id()/relation_type_id()/entity_type_id() run.
        var sourceId = SubstrateCanonicalIds.Source(sourceName);
        Hash128[]? relationIds = relationNames?.Select(RelationTypeRegistry.RelationTypeId).ToArray();

        Console.WriteLine(
            $"evicting testimony of {sourceName} "
            + (relationNames is null ? "(all relations discovered from evidence)" : $"({relationNames.Length} relation(s))")
            + " ...");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Eviction and its receipt go through NpgsqlSubstrateReader, shared by every interface.
        await using var ds = LaplaceDataSource.Create(SubstrateAccess.Ingest, ConnString);
        var reader = new NpgsqlSubstrateReader(ds);
        await reader.EvictSourceAsync(sourceId, relationIds, null);

        // Receipt: evidence rows still under the source (zero unless --relations restricted it).
        long remaining = await reader.CountEvidenceBySourceAsync(sourceId);
        sw.Stop();
        if (remaining != 0 && relationIds is null)
            return Fail($"evict {sourceName} failed verification: {remaining} testimony row(s) remain after unrestricted retraction.");
        Console.WriteLine(
            $"evict {sourceName} complete in {sw.Elapsed.TotalSeconds:F1}s — "
            + $"{remaining} evidence row(s) remaining under the source"
            + (remaining == 0 ? "" : " (restricted --relations leaves other relations in place)"));

        if (!rederive) return 0;

        Console.WriteLine($"re-deriving via `laplace ingest {ingestKey}` ...");
        return await IngestCommands.IngestAsync([ingestKey!]);
    }
}
