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
    public void DisplayLabels_DelegateToTheNativeBatchOwner()
    {
        var root = TypeIdLawTests.FindRepoRootPublic();
        var sql = File.ReadAllText(Path.Combine(root, "extension", "laplace_substrate", "sql",
            "functions", "converse", "label.sql.in"));
        var adapter = File.ReadAllText(Path.Combine(root, "extension", "laplace_substrate", "src", "display_native.c"));
        Assert.Contains("FROM public.laplace_labels(", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_laplace_display_label_batch", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SPI_execute", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("batch_text", adapter, StringComparison.Ordinal);
        Assert.Contains("pg_relation_metadata", adapter, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDisplay_HasAnExecutableFixtureForOrderRunsUnicodeAndPresence()
    {
        var root = TypeIdLawTests.FindRepoRootPublic();
        var fixture = File.ReadAllText(Path.Combine(root, "extension", "laplace_substrate", "tests", "sql", "display_label.sql"));
        Assert.Contains("INSERT INTO public.entity", fixture, StringComparison.Ordinal);
        Assert.Contains("realize.display_label_batch", fixture, StringComparison.Ordinal);
        Assert.Contains("WITH ORDINALITY", fixture, StringComparison.Ordinal);
        Assert.Contains("labels IS DISTINCT FROM", fixture, StringComparison.Ordinal);
        Assert.Contains("[dog · λ · 中]", fixture, StringComparison.Ordinal);
        Assert.Contains("type_id IS NULL", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO laplace.entities", fixture, StringComparison.Ordinal);
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
        Assert.Contains("Label = TrimGraphLabel(entry.Label, hex)", source, StringComparison.Ordinal);
        Assert.Contains("PreferredDisplayLabel", source, StringComparison.Ordinal);
        Assert.Contains("IdentityDisplayLabel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return \"Unrealized entity\";", source, StringComparison.Ordinal);
        Assert.DoesNotContain("?? \"Unrealized entity\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var lab = row.Label ?? hex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("r.Label ?? r.IdHex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NpgsqlSubstrateReads.LabelOrHexAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityFacets_KeepUnknownTypesUnknown_AndLexicalizationRemainsSeparate()
    {
        var root = TypeIdLawTests.FindRepoRootPublic();
        var catalog = File.ReadAllText(Path.Combine(root, "engine", "core", "src", "sql_catalog.def"));
        var facetsSql = File.ReadAllText(Path.Combine(root, "extension", "laplace_substrate", "sql",
            "functions", "inspect", "entity_facets.sql.in"));
        var realizeBatch = File.ReadAllText(Path.Combine(root, "extension", "laplace_substrate", "src",
            "realize_batch.c"));
        var synsetLemmaSql = File.ReadAllText(Path.Combine(root, "extension", "laplace_substrate", "sql",
            "functions", "realize", "realize_synset_lemma.sql.in"));

        Assert.Contains("public.entity e ON e.id=i.key", facetsSql, StringComparison.Ordinal);
        Assert.Contains("public.laplace_codepoint", facetsSql, StringComparison.Ordinal);
        Assert.Contains("ELSE NULL::bytea", facetsSql, StringComparison.Ordinal);
        Assert.DoesNotContain("laplace.entities", facetsSql, StringComparison.Ordinal);
        Assert.DoesNotContain("laplace.consensus", facetsSql, StringComparison.Ordinal);
        Assert.Contains("COALESCE(w.tier, 32767) ASC, cand.word_id", realizeBatch, StringComparison.Ordinal);
        Assert.Contains("realize.lexicalize_batch(ARRAY[p_id], p_lang)", synsetLemmaSql, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY", synsetLemmaSql, StringComparison.OrdinalIgnoreCase);
    }
}
