using global::Npgsql;
using Laplace.Engine.Core;
using NpgsqlTypes;
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
        var sourceId = SubstrateCanonicalIds.Source(sourceName).ToBytes();
        byte[][]? relationIds = relationNames?.Select(n => Hash128.OfCanonical(n).ToBytes()).ToArray();
        byte[][]? markerTypeIds = markerTypeNames?.Select(n => Hash128.OfCanonical(n).ToBytes()).ToArray();

        Console.WriteLine(
            $"evicting testimony of {sourceName} "
            + (relationNames is null ? "(all relations discovered from evidence)" : $"({relationNames.Length} relation(s))")
            + (markerTypeNames is null ? ", no marker cleanup" : $", markers: {string.Join(", ", markerTypeNames)}")
            + " ...");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var ds = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var conn = await ds.OpenConnectionAsync();
        await using (var call = conn.CreateCommand())
        {
            // The procedure COMMITs per batch and RAISE LOGs progress (server log) —
            // hours are legitimate on a large lane, so no timeout.
            call.CommandTimeout = 0;
            call.CommandText = "CALL laplace.evict_source($1, $2, $3)";
            call.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = sourceId });
            call.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                Value = (object?)relationIds ?? DBNull.Value,
            });
            call.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                Value = (object?)markerTypeIds ?? DBNull.Value,
            });
            await call.ExecuteNonQueryAsync();
        }

        // The receipt: zero surviving evidence rows under the source (cheap once true).
        long remaining;
        await using (var check = conn.CreateCommand())
        {
            check.CommandTimeout = 0;
            check.CommandText = "SELECT count(*) FROM laplace.attestations WHERE source_id = $1";
            check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = sourceId });
            remaining = (long)(await check.ExecuteScalarAsync() ?? 0L);
        }
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
