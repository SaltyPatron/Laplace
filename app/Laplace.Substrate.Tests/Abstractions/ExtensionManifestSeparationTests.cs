using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class ExtensionManifestSeparationTests
{
    private static string Root => TypeIdLawTests.FindRepoRootPublic();
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, .. parts]));

    [Fact]
    public void FreshInstallOwnsCoreTopology_UpgradeCannotRepartition()
    {
        var install = Read("extension", "laplace_substrate", "sql", "manifest.install");
        var upgrade = Read("extension", "laplace_substrate", "sql", "manifest.upgrade");

        Assert.Contains("generated/seed_relation_partitions.sql.in", install);
        Assert.DoesNotContain("drop_retired_", install);
        Assert.True(
            install.IndexOf("generated/seed_relation_partitions.sql.in", StringComparison.Ordinal)
            < install.IndexOf("bootstrap/bootstrap.sql.in", StringComparison.Ordinal),
            "fresh partition topology must exist before bootstrap deposits its first attestation");

        Assert.DoesNotContain("generated/seed_relation_partitions.sql.in", upgrade);
        Assert.DoesNotContain("schema/tables/entities.sql.in", upgrade);
        Assert.DoesNotContain("schema/tables/physicalities.sql.in", upgrade);
        Assert.DoesNotContain("schema/tables/attestations.sql.in", upgrade);
        Assert.DoesNotContain("schema/tables/consensus.sql.in", upgrade);
        Assert.DoesNotContain("indexes/physicalities_entity_btree.sql.in", upgrade);
        Assert.Contains("functions/identity/entity_interpretations_publish.sql.in", install);
        Assert.Contains("functions/identity/entity_interpretations_publish.sql.in", upgrade);
        Assert.Contains("drop_retired_", upgrade);
    }

    [Fact]
    public void GeneratedTopology_IsFreshOnlyAndSchemaQualified()
    {
        var topology = Read(
            "extension", "laplace_substrate", "sql", "generated",
            "seed_relation_partitions.sql.in");

        Assert.Contains("FROM laplace.consensus", topology);
        Assert.Contains("FROM laplace.attestations", topology);
        Assert.Contains("'laplace', part", topology);
        Assert.DoesNotContain("current_schema()", topology);
        Assert.DoesNotContain("DETACH PARTITION", topology);
        Assert.DoesNotContain("RETURNING *", topology);
        Assert.DoesNotContain("IF NOT EXISTS", topology);
    }

    [Fact]
    public void RecoveredIndexPaths_FailLoudWithoutSessionSearchPath()
    {
        var cycle = Read(
            "app", "Laplace.Substrate", "Crud", "Npgsql", "NpgsqlIndexCycle.cs");
        var entityIndex = Read(
            "extension", "laplace_substrate", "sql", "indexes",
            "physicalities_entity_btree.sql.in");
        var floor = Read("scripts", "check-substrate-floor.sh");

        Assert.DoesNotContain("SET search_path", cycle);
        Assert.DoesNotContain("CREATE INDEX IF NOT EXISTS", cycle);
        Assert.Contains("RebuildOneValidAsync", cycle);
        Assert.Contains("IndexValidityAsync", cycle);

        Assert.Contains("(entity_id, id)", entityIndex);
        Assert.DoesNotContain("IF NOT EXISTS", entityIndex);
        Assert.Contains("ops.index_health()", floor);
        Assert.DoesNotContain("pg_index", floor);
    }

    [Fact]
    public void CanonicalSeedWorkflow_KeepsSecondaryIndexesOnline()
    {
        var writer = Read(
            "app", "Laplace.Substrate", "Crud", "Npgsql", "NpgsqlWorkingSetApply.cs");
        var recovery = Read(
            "app", "Laplace.Substrate", "Crud", "Npgsql", "NpgsqlIndexCycle.cs");
        var program = Read("app", "Laplace.Cli", "Program.cs");
        var seedWorkflow = Read(".github", "workflows", "seed.yml");
        var foundation = Read("scripts", "ensure-foundation.sh");
        var foundationIndexes = Read("scripts", "foundation-bulk-indexes.sh");

        Assert.DoesNotContain("DropSecondariesAsync", recovery);
        Assert.DoesNotContain("JournalAndDropAsync", recovery);
        Assert.DoesNotContain("RecoveryDeferred", recovery);
        Assert.DoesNotContain("LAPLACE_INDEX_RECOVERY_DEFER", recovery);
        Assert.DoesNotContain("cycle.BeginAsync", writer);
        Assert.DoesNotContain("DropIndexesCommand", program);
        Assert.DoesNotContain("LAPLACE_INDEX_CYCLE", seedWorkflow);
        Assert.DoesNotContain("drop-indexes", seedWorkflow);
        Assert.DoesNotContain("foundation-bulk-indexes.sh\" begin", foundation);
        Assert.DoesNotContain("LAPLACE_INDEX_RECOVERY_DEFER", foundation);
        Assert.Contains("foundation-bulk-indexes.sh\" recover", foundation);
        Assert.DoesNotContain("DROP INDEX", foundationIndexes);
    }

    [Fact]
    public void EdgeRankIndex_IsInstalledAfterItsImmutableRankFunction()
    {
        foreach (var manifestName in new[] { "manifest.install", "manifest.upgrade" })
        {
            var manifest = Read("extension", "laplace_substrate", "sql", manifestName);
            Assert.True(
                manifest.IndexOf("functions/relation/relation_rank.sql.in", StringComparison.Ordinal)
                < manifest.IndexOf("indexes/consensus_edge_rank_btree.sql.in", StringComparison.Ordinal),
                $"{manifestName} must define consensus.relation_rank before its expression index");
        }
    }

    [Fact]
    public void EntityHighwayMasks_IsInstalledAfterItsParsedSqlDependencies()
    {
        string[] dependencies =
        [
            "functions/identity/relation_type_id.sql.in",
            "functions/highway/relation_highway.sql.in",
            "functions/highway/laplace_highway_ready.sql.in",
            "functions/highway/laplace_highway_mask_from_bits.sql.in",
        ];
        foreach (var manifestName in new[] { "manifest.install", "manifest.upgrade" })
        {
            var entries = Read("extension", "laplace_substrate", "sql", manifestName)
                .Split('\n', StringSplitOptions.TrimEntries);
            var dependent = Array.IndexOf(entries, "functions/generation/entity_highway_masks.sql.in");
            Assert.True(dependent >= 0, $"{manifestName} must install generation.entity_highway_masks");
            foreach (var dependency in dependencies)
            {
                var prerequisite = Array.IndexOf(entries, dependency);
                Assert.True(prerequisite >= 0 && prerequisite < dependent,
                    $"{manifestName} must install {dependency} before the parsed SQL body of generation.entity_highway_masks");
            }
        }
    }
}
