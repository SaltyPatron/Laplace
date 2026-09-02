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
    public void ChessRatingRepairRebuildsCompletePlayerRatingSurfaceFromDurableEvidence()
    {
        var sql = Read("extension", "laplace_substrate", "sql", "functions",
            "chess", "repair_player_ratings.sql.in");

        // The old repair inferred debt only from a missing/stale witness count or
        // timestamp. The runaway incident proved a corrupt rating can retain both,
        // so deployment must reconstruct the whole chess player rating surface from
        // authoritative testimony regardless of the current consensus values.
        Assert.DoesNotContain("IF NOT FOUND THEN\n        RETURN", sql,
            StringComparison.Ordinal);
        Assert.Contains("SELECT DISTINCT evidence.subject_id, evidence.type_id, evidence.object_id", sql,
            StringComparison.Ordinal);
        Assert.Contains("evidence.type_id = p_played", sql, StringComparison.Ordinal);
        Assert.Contains("evidence.type_id = p_outcome", sql, StringComparison.Ordinal);
        Assert.Contains("pairing.context_id IS NOT DISTINCT FROM evidence.context_id", sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("current.witness_count", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("current.last_observed_at", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY evidence.last_observed_at, evidence.id", sql,
            StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (id, type_id, subject_id) DO UPDATE", sql,
            StringComparison.Ordinal);
        Assert.Contains("target.witness_count, target.last_observed_at", sql,
            StringComparison.Ordinal);
        Assert.Contains("IS DISTINCT FROM", sql, StringComparison.Ordinal);
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