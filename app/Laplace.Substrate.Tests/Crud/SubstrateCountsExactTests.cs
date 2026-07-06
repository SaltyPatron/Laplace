using Xunit;
using Laplace.Decomposers.Abstractions.Tests;

namespace Laplace.Ingestion.Tests;

/// <summary>
/// I1: substrate_counts() must label planner estimates, not present as exact counts.
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
        Assert.DoesNotContain("'entities ~'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestCommands_StatusPrintsEstimateDisclaimer()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var ingest = Path.Combine(repoRoot, "app", "Laplace.Cli", "IngestCommands.cs");
        var text = File.ReadAllText(ingest);
        Assert.Contains("reltuples ESTIMATE", text, StringComparison.Ordinal);
        Assert.Contains("substrate_counts()", text, StringComparison.Ordinal);
    }
}
