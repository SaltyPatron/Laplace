using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The explorer exposes content identity separately from display text. A 128-bit content
/// address is useful for navigation/click identity, but it is never the user-facing label
/// when Laplace can render, describe, or type the entity.
/// </summary>
public sealed class ExploreDisplayLabelGateTests
{
    [Fact]
    public void DisplayLabels_AreSetWiseBoundedAndNeverUseTheHashAsTheLabel()
    {
        var root = TypeIdLawTests.FindRepoRootPublic();
        var path = Path.Combine(root, "app", "Laplace.Substrate", "Crud", "Npgsql",
            "NpgsqlDisplayLabels.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("realize.resolve_name_batch(@ids::bytea[])", source, StringComparison.Ordinal);
        Assert.Contains("realize.render_text_batch(b.ids, 3)", source, StringComparison.Ordinal);
        Assert.Contains("HAS_DEFINITION", source, StringComparison.Ordinal);
        Assert.Contains("HasFileMetadata", source, StringComparison.Ordinal);
        Assert.Contains("ST_PointN(w.trajectory, 1)", source, StringComparison.Ordinal);
        Assert.Contains("laplace_mantissa_unpack", source, StringComparison.Ordinal);
        Assert.Contains("type/source description", source, StringComparison.Ordinal);
        Assert.Contains("'Unrealized entity'", source, StringComparison.Ordinal);

        // A preview reads one trajectory point. It must never unpack or dump the whole
        // document merely to put text on a graph node.
        Assert.DoesNotContain("ST_DumpPoints", source, StringComparison.Ordinal);
        Assert.DoesNotContain("generation.trajectory_unpacked_points", source, StringComparison.Ordinal);
        Assert.DoesNotContain("realize.render_text_batch(@ids::bytea[], 0)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExploreSurfaces_UseDisplayLabelsAndKeepHashOnlyAsIdentity()
    {
        var root = TypeIdLawTests.FindRepoRootPublic();
        var path = Path.Combine(root, "app", "Laplace.Endpoints.OpenAICompat",
            "SubstrateClient.Explore.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("NpgsqlDisplayLabels.ReadAsync", source, StringComparison.Ordinal);
        Assert.Contains("NpgsqlDisplayLabels.ReadOneAsync", source, StringComparison.Ordinal);
        Assert.Contains("NpgsqlDisplayLabels.FacetAsync", source, StringComparison.Ordinal);
        Assert.Contains("StringInfo.ParseCombiningCharacters", source, StringComparison.Ordinal);
        Assert.Contains("Label = TrimGraphLabel(entry.Label)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var lab = row.Label ?? hex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("r.Label ?? r.IdHex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NpgsqlSubstrateReads.LabelOrHexAsync", source, StringComparison.Ordinal);
    }
}
