using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class ConsensusMutationRoutingTests
{
    private static string RepoRoot => TypeIdLawTests.FindRepoRootPublic();

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. path]));

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
    public void DecayAndPruneMutateConcreteLeavesOnly()
    {
        var decay = Read("extension", "laplace_substrate", "sql", "functions",
            "inference", "laplace_decay.sql.in");
        var prune = Read("extension", "laplace_substrate", "sql", "functions",
            "inference", "laplace_prune.sql.in");

        Assert.Contains("consensus.partition_leaf(p_type, p_subject)", decay,
            StringComparison.Ordinal);
        Assert.Contains("UPDATE ONLY %s", decay, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE laplace.consensus", decay,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains("pg_partition_tree('laplace.consensus'::regclass)", prune,
            StringComparison.Ordinal);
        Assert.Contains("DELETE FROM ONLY %s", prune, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM laplace.consensus", prune,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceEvictionRoutesReplacementAndCullingByPrimaryKeyAndLeaf()
    {
        var sql = Read("extension", "laplace_substrate", "sql", "functions",
            "ops", "evict_source.sql.in");

        Assert.Contains("consensus.partition_leaf(%L::bytea, d.subject_id)::oid", sql,
            StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (id, type_id, subject_id) DO UPDATE", sql,
            StringComparison.Ordinal);
        Assert.Contains("DELETE FROM ONLY %s c", sql, StringComparison.Ordinal);
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

        Assert.Equal([
            "extension/laplace_substrate/sql/functions/chess/repair_player_ratings.sql.in",
            "extension/laplace_substrate/sql/functions/ops/evict_source.sql.in",
            "extension/laplace_substrate/sql/functions/ops/refold_source.sql.in",
        ], writers);

        var repair = File.ReadAllText(Path.Combine(RepoRoot, writers[0]));
        var evict = File.ReadAllText(Path.Combine(RepoRoot, writers[1]));
        var refold = File.ReadAllText(Path.Combine(RepoRoot, writers[2]));
        Assert.Contains("ON CONFLICT (id, type_id, subject_id) DO UPDATE", repair,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(?:UPDATE|DELETE\s+FROM)\s+laplace\.consensus\b", evict);
        Assert.Contains("ON CONFLICT (id, type_id, subject_id) DO UPDATE", evict,
            StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (id, type_id, subject_id) DO UPDATE", refold,
            StringComparison.Ordinal);
    }
}
