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
    public void DisplayLabels_AreInstalledSetWiseBoundedAndNeverUseTheHashAsTheLabel()
    {
        var root = TypeIdLawTests.FindRepoRootPublic();
        var sqlPath = Path.Combine(root, "extension", "laplace_substrate", "sql", "functions",
            "converse", "label.sql.in");
        var sql = File.ReadAllText(sqlPath);
        var appPath = Path.Combine(root, "app", "Laplace.Substrate", "Crud", "Npgsql",
            "NpgsqlDisplayLabels.cs");
        var app = File.ReadAllText(appPath);

        Assert.Contains("CREATE OR REPLACE FUNCTION realize.display_label_batch", sql, StringComparison.Ordinal);
        Assert.Contains("realize.resolve_name_batch(p_ids)", sql, StringComparison.Ordinal);
        Assert.Contains("realize.render_text_batch(b.ids)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("realize.render_text_batch(b.ids, 3)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("realize.render_text_batch(b.ids, 4)", sql, StringComparison.Ordinal);
        Assert.Contains("consensus.relation_family_members('HAS_DEFINITION')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("consensus.relation_family_ids('HAS_DEFINITION')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("relation_type_id('HAS_DEFINITION')", sql, StringComparison.Ordinal);
        Assert.Contains("HasFileMetadata", sql, StringComparison.Ordinal);
        Assert.Contains("WITH RECURSIVE", sql, StringComparison.Ordinal);
        Assert.Contains("preview_spine", sql, StringComparison.Ordinal);
        Assert.Contains("ST_PointN(w.trajectory, 1)", sql, StringComparison.Ordinal);
        Assert.Contains("laplace_mantissa_unpack", sql, StringComparison.Ordinal);
        Assert.Contains("s.depth < 32", sql, StringComparison.Ordinal);
        Assert.Contains("'Unrealized entity'", sql, StringComparison.Ordinal);
        Assert.Contains("entity.type_id", sql, StringComparison.Ordinal);
        Assert.Contains("never decides which projection runs", sql, StringComparison.OrdinalIgnoreCase);

        // A preview follows one ordered branch at each tier. It must never unpack siblings or
        // reconstruct a full book/document merely to put text on a graph node, and the app
        // must consume the installed projection rather than owning a second copy of the law.
        // The display function also must not create a pg_depend edge to the generated
        // relation_family_ids helper: extension upgrades regenerate that helper later in the
        // manifest and older upgrades still DROP it before recreating it.
        Assert.DoesNotContain("ST_DumpPoints", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("generation.trajectory_unpacked_points", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("constituents_closure", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("render_text_batch(p_ids, 0)", sql, StringComparison.Ordinal);
        Assert.Contains("realize.display_label_batch(@ids::bytea[])", app, StringComparison.Ordinal);
        Assert.DoesNotContain("HAS_DEFINITION", app, StringComparison.Ordinal);
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
