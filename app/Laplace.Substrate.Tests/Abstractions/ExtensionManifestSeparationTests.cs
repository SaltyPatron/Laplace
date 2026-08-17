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

        Assert.DoesNotContain("generated/seed_relation_partitions.sql.in", upgrade);
        Assert.DoesNotContain("schema/tables/entities.sql.in", upgrade);
        Assert.DoesNotContain("schema/tables/physicalities.sql.in", upgrade);
        Assert.DoesNotContain("schema/tables/attestations.sql.in", upgrade);
        Assert.DoesNotContain("schema/tables/consensus.sql.in", upgrade);
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
}
