using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Human-facing read surfaces keep content identity and presentation separate.
/// A BLAKE3 id is the exact navigation handle; it is not a label of last resort.
/// These gates also pin the bounded preview shape so fixing readability cannot
/// regress into recursively rendering whole documents on a graph request.
/// </summary>
public sealed class DisplayLabelContractTests
{
    private static string RepoRoot => TypeIdLawTests.FindRepoRootPublic();

    [Fact]
    public void DisplayLabels_UseNamesUnicodePreviewTypeThenAbstain()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot, "app", "Laplace.Substrate", "Crud", "Npgsql", "NpgsqlDisplayReads.cs"));

        Assert.Contains("realize.resolve_name_batch", source, StringComparison.Ordinal);
        Assert.Contains("realize.render_text_batch(s.ids, 3)", source, StringComparison.Ordinal);
        Assert.Contains("realize.render_text_batch(p.ids, 3)", source, StringComparison.Ordinal);
        Assert.Contains("lexical.type_label_batch", source, StringComparison.Ordinal);
        Assert.Contains("'unrealized entity'", source, StringComparison.Ordinal);

        // High-tier preview follows one ordered trunk-to-leaf path. It must not ask
        // for a full recursive closure simply to name one visualization node.
        Assert.Contains("FROM realize.constituents(s.node_id)", source, StringComparison.Ordinal);
        Assert.Contains("ORDER BY k.ordinal", source, StringComparison.Ordinal);
        Assert.Contains("LIMIT 1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("constituents_closure", source, StringComparison.Ordinal);

        // PostgreSQL text functions count characters, so non-BMP Unicode cannot be
        // split by a .NET UTF-16 code-unit slice at the presentation boundary.
        Assert.Contains("char_length(r.label)", source, StringComparison.Ordinal);
        Assert.Contains("left(r.label, 47)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExploreGraph_NeverPromotesIdentityHashIntoVisibleLabel()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot, "app", "Laplace.Endpoints.OpenAICompat", "SubstrateClient.Explore.cs"));

        Assert.Contains("NpgsqlDisplayReads.DisplayLabelsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ExploreGraphNode(sourceHex, sourceHex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ExploreGraphNode(objectHex, objectHex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("row.Label ?? hex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("r.Label ?? r.IdHex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lab[..47]", source, StringComparison.Ordinal);
    }
}
