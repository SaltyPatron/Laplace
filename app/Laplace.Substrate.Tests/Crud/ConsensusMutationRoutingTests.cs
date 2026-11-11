using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class ConsensusMutationRoutingTests
{
    private static string RepoRoot => TypeIdLawTests.FindRepoRootPublic();

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. path])) + (path[^1].EndsWith(".def", StringComparison.Ordinal) && Directory.Exists(Path.Combine([RepoRoot, .. path[..^1], Path.GetFileNameWithoutExtension(path[^1])])) ? "\n" + string.Join("\n", Directory.GetFiles(Path.Combine([RepoRoot, .. path[..^1], Path.GetFileNameWithoutExtension(path[^1])]), "*.def").OrderBy(f => f).Select(File.ReadAllText)) : "");

    [Fact]
    public void SingleWitnessUsesTheNativeKeyedWriter()
    {
        var sql = Read("extension", "laplace_substrate", "sql", "functions",
            "inference", "laplace_witness.sql.in");

        Assert.Contains("consensus.upsert_type(", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM laplace.consensus", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE laplace.consensus", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO laplace.consensus", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecayAndPruneAreDropOnlyRetiredSurfaces()
    {
        var decay = Read("extension", "laplace_substrate", "sql", "functions",
            "inference", "laplace_decay.sql.in");
        var prune = Read("extension", "laplace_substrate", "sql", "functions",
            "inference", "laplace_prune.sql.in");

        Assert.Contains("DROP FUNCTION IF EXISTS generation.decay", decay,
            StringComparison.Ordinal);
        Assert.Contains("DROP FUNCTION IF EXISTS laplace.laplace_decay", decay,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE OR REPLACE FUNCTION", decay,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", decay, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO ", decay, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("DROP FUNCTION IF EXISTS generation.prune", prune,
            StringComparison.Ordinal);
        Assert.Contains("DROP FUNCTION IF EXISTS laplace.laplace_prune", prune,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE OR REPLACE FUNCTION", prune,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM ", prune, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", prune, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceEvictionUsesTheNativeEvidenceRetractionOwner()
    {
        var sql = Read("extension", "laplace_substrate", "sql", "functions",
            "ops", "evict_source.sql.in");

        Assert.Contains("SELECT consensus.refold_after_retraction_type(", sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("consensus.partition_leaf(", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO laplace.consensus", sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE laplace.consensus", sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM laplace.consensus", sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionConsensusMutationAuthorityHasNoOtherSqlWriters()
    {
        var sqlRoot = Path.Combine(RepoRoot, "extension", "laplace_substrate", "sql");
        var directParentMutation = new Regex(
            @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\s+laplace\.consensus\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var writers = Directory.EnumerateFiles(sqlRoot, "*.sql.in", SearchOption.AllDirectories)
            .Where(path => directParentMutation.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // SQL orchestrates admission/refold/retraction through one native
        // lock/snapshot owner; it has no independent standing writer.
        Assert.Empty(writers);

        var evict = Read("extension", "laplace_substrate", "sql", "functions",
            "ops", "evict_source.sql.in");
        var refold = Read("extension", "laplace_substrate", "sql", "functions",
            "ops", "refold_source.sql.in");
        Assert.DoesNotMatch(@"\b(?:UPDATE|DELETE\s+FROM)\s+laplace\.consensus\b", evict);
        Assert.Contains("SELECT consensus.refold_after_retraction_type(", evict,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(directParentMutation, refold);
        Assert.Contains("SELECT consensus.refold_evidence_type(", refold,
            StringComparison.Ordinal);
        Assert.DoesNotContain("laplace.consensus_fold(", refold, StringComparison.Ordinal);
        var entry = Read("extension", "laplace_substrate", "sql", "functions",
            "fold", "consensus_upsert.sql.in");
        Assert.Contains("CREATE OR REPLACE FUNCTION consensus.merge_evidence(", entry);
        Assert.Contains("AS 'EXECUTION_LIBRARY', 'pg_laplace_consensus_merge_evidence'", entry);
        Assert.Contains("CREATE OR REPLACE FUNCTION consensus.upsert_evidence_type(", entry);
        Assert.Contains("AS 'EXECUTION_LIBRARY', 'pg_laplace_consensus_upsert_evidence_type'", entry);
        Assert.Contains("CREATE OR REPLACE FUNCTION consensus.refold_evidence_type(", entry);
        Assert.Contains("AS 'EXECUTION_LIBRARY', 'pg_laplace_consensus_refold_evidence_type'", entry);
        Assert.Contains("CREATE OR REPLACE FUNCTION consensus.refold_after_retraction_type(", entry);
        Assert.Contains("AS 'EXECUTION_LIBRARY', 'pg_laplace_consensus_refold_after_retraction_type'", entry);
    }
}
