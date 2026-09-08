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
        Assert.Contains("pg_laplace_repair_player_ratings_batch", sql, StringComparison.Ordinal);
        Assert.Contains("LANGUAGE C", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("current.witness_count", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("current.last_observed_at", sql, StringComparison.Ordinal);
        var native = Read("extension", "laplace_substrate", "src", "chess_rating_repair.c");
        var catalog = Read("engine", "core", "src", "sql_catalog.def");
        Assert.Contains("PG_GETARG_DATUM(0), PG_GETARG_DATUM(1)", native);
        Assert.Contains("qsort(evidence, n, sizeof(RepairEvidence), evidence_compare)", native);
        Assert.Contains("glicko2_fold_uniform_period", native);
        Assert.Contains("consensus.repair_evidence", native);
        Assert.Contains("consensus.repair_write", native);
        Assert.Contains("WHERE subject_id = ANY($1) AND type_id = ANY($2)", catalog);
        Assert.Contains("WHERE ROW(c.rating,c.rd,c.volatility,c.witness_count,c.last_observed_at) IS DISTINCT FROM", catalog);
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
            "extension/laplace_substrate/sql/functions/ops/evict_source.sql.in",
            "extension/laplace_substrate/sql/functions/ops/refold_source.sql.in",
        ], writers);

        var evict = File.ReadAllText(Path.Combine(RepoRoot, writers[0]));
        var refold = File.ReadAllText(Path.Combine(RepoRoot, writers[1]));
        Assert.DoesNotMatch(@"\b(?:UPDATE|DELETE\s+FROM)\s+laplace\.consensus\b", evict);
        Assert.Contains("ON CONFLICT (id, type_id, subject_id) DO UPDATE", evict,
            StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (id, type_id, subject_id) DO UPDATE", refold,
            StringComparison.Ordinal);
    }
}
