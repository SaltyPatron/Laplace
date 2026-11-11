using Laplace.Decomposers.Abstractions.Tests;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class StaticSubstrateIsaGateTests
{
    private static string Read(params string[] parts)
    {
        var root = TypeIdLawTests.FindRepoRootPublic();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }

    [Fact]
    public void PresenceOrdinals_AreFixedSetSurfaces_NotAlternateOrchestrators()
    {
        var entities = Read("extension", "laplace_substrate", "sql", "probes",
            "entities_present_ordinals.sql.in");
        var physicalities = Read("extension", "laplace_substrate", "sql", "probes",
            "physicalities_present_ordinals.sql.in");
        var attestations = Read("extension", "laplace_substrate", "sql", "probes",
            "attestations_present_ordinals.sql.in");

        foreach (var sql in new[] { entities, physicalities, attestations })
        {
            Assert.Contains("PARALLEL RESTRICTED", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("PARALLEL SAFE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("LANGUAGE plpgsql", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CREATE TEMP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EXECUTE format", sql, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("p_tiers smallint[]", entities, StringComparison.Ordinal);
        Assert.DoesNotContain("p_hilberts bytea[]", physicalities, StringComparison.Ordinal);
        Assert.DoesNotContain("p_type_ids bytea[]", attestations, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("generation.decay")]
    [InlineData("generation.prune")]
    public void DerivedConsensus_CannotExposeDirectMutationInferenceVerbs(string function)
    {
        var root = TypeIdLawTests.FindRepoRootPublic();
        var sql = Path.Combine(root, "extension", "laplace_substrate", "sql");
        foreach (var file in Directory.EnumerateFiles(sql, "*.sql.in", SearchOption.AllDirectories))
            Assert.DoesNotContain($"FUNCTION {function}(", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
