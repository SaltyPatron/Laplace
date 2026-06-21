using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The AST is the merkle DAG, and a grammar's leaf text continues that same DAG downward. So a JSON
/// scalar leaf (Wiktionary etc.) and the content path (WordNet/OMW/VerbNet via ContentWitnessBatch)
/// must resolve the SAME surface to the SAME entity id, or the graph fragments at the grammar↔text
/// seam. The compose path routes JSON leaves through laplace_content_root_id specifically to make
/// this hold by construction (grammar_compose.cpp); this test is the regression guard for that.
/// ASCII surfaces are used so NFC normalization is identity and a char index equals a byte index.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class JsonLeafContentConvergenceTests
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/json-leaf-convergence/v1");

    [Theory]
    [InlineData("cat")]        // single word, multi-grapheme
    [InlineData("New York")]   // multi-word surface (the audit's example)
    public void JsonStringLeaf_Id_ConvergesWith_ContentPath(string surface)
    {
        string doc = "{\"w\":\"" + surface + "\"}";
        byte[] utf8 = Encoding.UTF8.GetBytes(doc);
        int ci = doc.IndexOf(surface, StringComparison.Ordinal);

        using var ast = GrammarDecomposer.Parse(utf8, "json");
        using var composer = new GrammarRowComposer(utf8, ast, Src, "json");

        Assert.True(
            composer.TrySpanEntity((uint)ci, (uint)(ci + surface.Length), out var leafId),
            "the JSON string-content leaf must resolve to a composed entity");

        var contentId = ContentWitnessBatch.RootId(surface);
        Assert.NotNull(contentId);
        Assert.Equal(contentId!.Value, leafId);
    }
}
