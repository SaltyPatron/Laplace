using System.Text.Json;
using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.Data.Tests;

[Trait("Category", "RequiresNetwork")]
[Trait("Tier", "live")]
public sealed class LiveSubstrateQueriesTests
{
    private static async Task<NpgsqlDataSource?> TryOpenLiveDataSourceAsync()
    {
        List<string> candidates = [];
        var explicitConn = Environment.GetEnvironmentVariable("LAPLACE_TEST_CONNINFO");
        if (!string.IsNullOrWhiteSpace(explicitConn))
            candidates.Add(explicitConn);
        try
        {
            var configured = LaplaceInstall.PostgresConnectionString();
            if (!string.IsNullOrWhiteSpace(configured))
                candidates.Add(configured);
        }
        catch (InvalidOperationException)
        {
        }
        candidates.Add("Host=127.0.0.1;Port=5432;Username=ahart;Database=laplace;Timeout=3;Command Timeout=15");

        foreach (var connString in candidates.Distinct(StringComparer.Ordinal))
        {
            NpgsqlDataSource? ds = null;
            try
            {
                ds = NpgsqlDataSource.Create(connString);
                await using var conn = await ds.OpenConnectionAsync();
                await using var cmd = new NpgsqlCommand("SELECT 1 FROM public.consensus LIMIT 1", conn);
                _ = await cmd.ExecuteScalarAsync();
                return ds;
            }
            catch
            {
                if (ds is not null)
                    await ds.DisposeAsync();
            }
        }
        return null;
    }

    private static async Task<byte[]> ResolveTextIdAsync(NpgsqlConnection conn, string text)
    {
        await using var cmd = new NpgsqlCommand("SELECT public.blake3_send(public.laplace_text_id($1))", conn);
        cmd.Parameters.AddWithValue(text);
        return (byte[])(await cmd.ExecuteScalarAsync())!;
    }

    [SkippableFact]
    public async Task CoreReadMethods_ExecuteWithoutSqlErrorsOnLiveDatabase()
    {
        await using var ds = await TryOpenLiveDataSourceAsync();
        Skip.If(ds is null, "Live laplace PostgreSQL database not reachable.");

        await using var conn = await ds!.OpenConnectionAsync();
        var witnesses = await NpgsqlSubstrateReads.SourceCountsApproxAsync(conn, CancellationToken.None, timeoutSeconds: 10);
        Assert.NotEmpty(witnesses);

        var relations = await NpgsqlSubstrateReads.TopRelationsAsync(conn, limit: 10, CancellationToken.None);
        Assert.NotNull(relations);

        var dogId = await ResolveTextIdAsync(conn, "dog");
        var record = await NpgsqlSubstrateReads.EntityRecordAsync(ds, dogId, CancellationToken.None);
        Assert.True(record.Confirmed + record.Contested + record.Refuted + record.Thin >= 0);

        var web = await NpgsqlSubstrateReads.ExploreWebAsync(
            conn, dogId, hops: 1, fanout: 6, maxNodes: 32, timeoutSeconds: 10, CancellationToken.None);
        Assert.NotNull(web);

        var taxonomy = await NpgsqlSubstrateReads.TaxonomyTreeAsync(
            ds, dogId, depth: 2, childLimit: 10, CancellationToken.None);
        Assert.NotNull(taxonomy);

        var translations = await NpgsqlSubstrateReads.TranslationsAsync(
            ds, "dog", limit: 10, CancellationToken.None);
        Assert.NotNull(translations);

        var completions = await NpgsqlSubstrateReads.CompletionsAsync(
            conn, "dog", limit: 5, CancellationToken.None);
        Assert.NotNull(completions);

        var catId = await ResolveTextIdAsync(conn, "cat");

        var sourceStatus = await NpgsqlSubstrateReads.SourceStatusAsync(
            ds, source: null, CancellationToken.None);
        Assert.NotEmpty(sourceStatus);

        _ = await NpgsqlSubstrateReads.EvidenceCountAsync(conn, dogId, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.EvidenceSeriesReceiptAsync(conn, dogId, limit: 10, inbound: false, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.EvidenceSeriesReceiptAsync(conn, dogId, limit: 10, inbound: true, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.EvidenceCountsBatchAsync(conn, [dogId, catId], CancellationToken.None);
        _ = await NpgsqlSubstrateReads.AttestationsOutBatchAsync(conn, [dogId, catId], perId: 5, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.EvidenceForTargetAsync(conn, "dog", limit: 10, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.RelationSummaryAsync(ds, dogId, catId, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.ContrastAsync(ds, dogId, catId, limit: 10, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.SalientFactsAsync(ds, dogId, limit: 6, CancellationToken.None);
        var ingestedSource = sourceStatus.FirstOrDefault(s => s.Ingested, sourceStatus[0]);
        _ = await NpgsqlSubstrateReads.SourceRosterAsync(ds, ingestedSource.SourceId, limit: 5, CancellationToken.None);

        var salient = await NpgsqlSubstrateReads.ResolveSalientWordAsync(
            ds, ["what", "is", "photosynthesis"], CancellationToken.None);
        Assert.NotNull(salient);
        Assert.Equal("photosynthesis", salient!.Label);

        var photoIntent = await NpgsqlSubstrateReads.RecallIntentAsync(
            ds, "define", salient.Id, null, null, null, null, CancellationToken.None);
        Assert.Contains(photoIntent, r => r.Reply.Contains("synthesis of compounds", StringComparison.OrdinalIgnoreCase));

        _ = await NpgsqlSubstrateReads.RecallSessionAsync(conn, "photosynthesis", null, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.ExplainTraceStepsAsync(conn, "photosynthesis", depth: 2, beam: 8, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.WalkBranchesAsync(ds, "photosynthesis", null, null, depth: 2, breadth: 8, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.BandFactsAsync(ds, dogId, bands: null, limit: 10, CancellationToken.None);
        _ = await NpgsqlSubstrateReads.WalkBranchesBeamAsync(ds, dogId, relationType: null, bands: null, depth: 2, breadth: 8, limit: 10, CancellationToken.None);
    }

    [SkippableFact]
    public async Task NamedEntitiesAsync_DiscoversCaseVariantsForKing()
    {
        await using var ds = await TryOpenLiveDataSourceAsync();
        Skip.If(ds is null, "Live laplace PostgreSQL database not reachable.");

        await using var conn = await ds!.OpenConnectionAsync();
        var kingLower = await ResolveTextIdAsync(conn, "king");
        var kingTitle = await ResolveTextIdAsync(conn, "King");
        var kingUpper = await ResolveTextIdAsync(conn, "KING");

        var hits = await NpgsqlBrowseReads.NamedEntitiesAsync(
            conn,
            memberIds: [kingLower, kingTitle, kingUpper],
            exactRootId: kingLower,
            variantRootIds: [kingTitle, kingUpper],
            discover: true,
            offset: 0,
            limit: 25,
            candidateCapacity: 2048,
            CancellationToken.None);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.MatchKind is "exact" or "case_variant" or "member");
    }

    [SkippableFact]
    public async Task LeaderboardAsync_UsesConsensusStandingIndexAndStaysUnder50ms()
    {
        await using var ds = await TryOpenLiveDataSourceAsync();
        Skip.If(ds is null, "Live laplace PostgreSQL database not reachable.");

        await using var conn = await ds!.OpenConnectionAsync();
        foreach (var sort in new[] { "standing", "rating", "matches" })
        {
            var page = await NpgsqlBrowseReads.LeaderboardAsync(
                conn, offset: 0, limit: 25, sort: sort, tier: null, CancellationToken.None);
            Assert.NotEmpty(page.Rows);
        }

        await using var explainCmd = new NpgsqlCommand(
            """
            EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
            SELECT c.claim,
                   public.laplace_confidence(c.rating, c.deviation, 2::double precision) AS confidence,
                   c.rating,
                   c.deviation,
                   c.matches
            FROM public.consensus c
            ORDER BY public.laplace_confidence(c.rating, c.deviation, 2::double precision) DESC, c.matches DESC, c.claim
            LIMIT 25
            """,
            conn);
        var planJson = (string)(await explainCmd.ExecuteScalarAsync())!;
        using var doc = JsonDocument.Parse(planJson);
        var root = doc.RootElement[0];
        double executionMs = root.GetProperty("Execution Time").GetDouble();
        Assert.Contains("consensus_standing", planJson, StringComparison.OrdinalIgnoreCase);
        Assert.True(executionMs < 50.0, $"Expected consensus_standing index scan < 50 ms, measured {executionMs:F2} ms.");
    }

    [SkippableFact]
    public async Task InstalledOps_RunCleanlyViaInvoker()
    {
        await using var ds = await TryOpenLiveDataSourceAsync();
        Skip.If(ds is null, "Live laplace PostgreSQL database not reachable.");

        foreach (var opName in new[] { "ops.source_status", "ops.ingest_runs", "ops.activity", "ops.index_health" })
        {
            var res = await InstalledOpInvoker.InvokeAsync(ds!, opName, args: null, maxRows: 25, ct: CancellationToken.None);
            Assert.True(res.Error is null, $"{opName} returned error: {res.Error}");
        }
    }
}
