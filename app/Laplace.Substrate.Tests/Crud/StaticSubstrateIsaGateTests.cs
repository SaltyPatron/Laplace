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

        foreach (var sql in new[] { entities, physicalities })
        {
            Assert.Contains("PARALLEL RESTRICTED", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("PARALLEL SAFE", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("LANGUAGE plpgsql", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CREATE TEMP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EXECUTE format", sql, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("p_tiers smallint[]", entities, StringComparison.Ordinal);
        Assert.DoesNotContain("p_hilberts bytea[]", physicalities, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("laplace_decay.sql.in", "generation.decay")]
    [InlineData("laplace_prune.sql.in", "generation.prune")]
    public void DerivedConsensus_CannotExposeDirectMutationInferenceVerbs(
        string file, string function)
    {
        var sql = Read("extension", "laplace_substrate", "sql", "functions", "inference", file);
        Assert.Contains($"DROP FUNCTION IF EXISTS {function}", sql, StringComparison.Ordinal);
        Assert.DoesNotContain($"CREATE OR REPLACE FUNCTION {function}", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE ONLY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM ONLY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXECUTE format", sql, StringComparison.OrdinalIgnoreCase);
    }
}
