using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class RefoldSourceRegressionTests
{
    [Fact]
    public void RefoldSource_RebuildsMissingRowsWithCanonicalConsensusIdentity()
    {
        var repoRoot = TypeIdLawTests.FindRepoRootPublic();
        var sql = File.ReadAllText(Path.Combine(
            repoRoot,
            "extension", "laplace_substrate", "sql", "functions", "ops",
            "refold_source.sql.in"));

        // Refold is a reconstruction from durable testimony, not merely an update
        // of already-materialized derived rows. An interrupted first fold may leave
        // no consensus row to UPDATE (#1349/#1292).
        Assert.Contains("INSERT INTO laplace.consensus", sql, StringComparison.Ordinal);
        Assert.Contains(
            "laplace.consensus_id(f.subject_id, %L::bytea, f.object_id)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ON CONFLICT (id, type_id, subject_id) DO UPDATE",
            sql,
            StringComparison.Ordinal);

        // The rebuild must remain evidence-derived and deterministic: same neutral
        // prior, same fixed-point observations, same canonical evidence ordering.
        Assert.Contains("laplace.consensus_fold(false, NULL, NULL, NULL,", sql, StringComparison.Ordinal);
        Assert.Contains("GREATEST(a.observation_count, 1)", sql, StringComparison.Ordinal);
        Assert.Contains("a.sum_score_fp1e9", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY a.last_observed_at, a.id", sql, StringComparison.Ordinal);

        // Recovery must never rewrite the source testimony it is reconstructing from.
        Assert.DoesNotContain("UPDATE laplace.attestations", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM laplace.attestations", sql, StringComparison.OrdinalIgnoreCase);
    }
}
