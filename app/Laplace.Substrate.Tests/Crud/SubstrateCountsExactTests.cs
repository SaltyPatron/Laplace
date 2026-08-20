using Xunit;
using Laplace.Decomposers.Abstractions.Tests;

namespace Laplace.Ingestion.Tests;

/// <summary>
/// I1: ops.substrate_counts() must label planner estimates, not present as exact counts.
/// </summary>
public sealed class SubstrateCountsExactTests
{
    [Fact]
    public void SubstrateCountsSql_LabelsMetricsAsEstimate()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var sql = Path.Combine(
            repoRoot, "extension", "laplace_substrate", "sql", "functions", "ops", "substrate_counts.sql.in");
        Assert.True(File.Exists(sql), "substrate_counts.sql.in must exist");
        var text = File.ReadAllText(sql);
        Assert.Contains("(ESTIMATE)", text, StringComparison.Ordinal);
        Assert.Contains("pg_class.reltuples", text, StringComparison.Ordinal);
        Assert.Contains("pg_partition_tree", text, StringComparison.Ordinal);
        Assert.Contains("WHERE p.isleaf", text, StringComparison.Ordinal);
        Assert.DoesNotContain("'entities ~'", text, StringComparison.Ordinal);
        Assert.Contains("ops.substrate_counts()", text, StringComparison.Ordinal);
        Assert.Contains("'entities(ESTIMATE)'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("'laplace.entities(ESTIMATE)'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestCommands_StatusPrintsEstimateDisclaimer()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var ingest = Path.Combine(repoRoot, "app", "Laplace.Cli", "IngestCommands.cs");
        var text = File.ReadAllText(ingest);
        Assert.Contains("reltuples ESTIMATE", text, StringComparison.Ordinal);
        Assert.Contains("ops.substrate_counts()", text, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestPartitionPressure_UsesPlannerStatisticsInsteadOfScanningTheCorpus()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var sql = Path.Combine(repoRoot, "extension", "laplace_substrate", "sql",
            "functions", "inspect", "consensus_partition_pressure.sql.in");
        var text = File.ReadAllText(sql);

        Assert.Contains("pg_stats", text, StringComparison.Ordinal);
        Assert.Contains("reltuples", text, StringComparison.Ordinal);
        Assert.Contains("most_common_freqs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM laplace.consensus_rdefault", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM laplace.attestations_rdefault", text, StringComparison.Ordinal);
        Assert.DoesNotContain("min_rows", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoverIndexes_RefreshesEveryCoreTableIncludingPhysicalities()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var ingestOps = Path.Combine(
            repoRoot, "app", "Laplace.Substrate", "Crud", "Npgsql", "NpgsqlIngestOps.cs");
        var text = File.ReadAllText(ingestOps);

        Assert.Contains("ANALYZE laplace.attestations", text, StringComparison.Ordinal);
        Assert.Contains("ANALYZE laplace.consensus", text, StringComparison.Ordinal);
        Assert.Contains("ANALYZE laplace.entities", text, StringComparison.Ordinal);
        Assert.Contains("ANALYZE laplace.physicalities (entity_id, type)", text, StringComparison.Ordinal);
    }
}
