using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class JsonLeafContentConvergenceTests
{
    private static readonly Hash128 Src =
        Hash128.OfCanonical("substrate/source/test/json-leaf-convergence/v1");

    [Theory]
    [InlineData("cat")]
    [InlineData("New York")]
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

        var contentId = ContentTierSpine.ResolveRoot(surface);
        Assert.NotNull(contentId);
        Assert.Equal(contentId!.Value, leafId);
    }

    [Fact]
    public void JsonStringLeaf_EscapedUnicode_ConvergesWith_ContentPath()
    {
        const string surface = "caf\u00e9";
        string doc = "{\"w\":\"caf\\u00e9\"}";
        byte[] utf8 = Encoding.UTF8.GetBytes(doc);

        using var ast = GrammarDecomposer.Parse(utf8, "json");
        using var composer = new GrammarRowComposer(utf8, ast, Src, "json");
        var ctx = new GrammarComposeContext(
            utf8, ast, default, composer, JsonGrammarHelper.FindRootObjectNode(ast));

        Assert.True(JsonGrammarHelper.TryComposedProperty(ctx, "w", out var leafId));
        var contentId = ContentTierSpine.ResolveRoot(surface);
        Assert.NotNull(contentId);
        Assert.Equal(contentId!.Value, leafId);
    }

    [Fact]
    public void JsonGrammarHelper_PropertyLookup_UsesContentRoot_NotComposeMerkle()
    {
        const string surface = "New York";
        string doc = "{\"word\":\"" + surface + "\"}";
        byte[] utf8 = Encoding.UTF8.GetBytes(doc);

        using var ast = GrammarDecomposer.Parse(utf8, "json");
        using var composer = new GrammarRowComposer(utf8, ast, Src, "json");
        var ctx = new GrammarComposeContext(
            utf8, ast, default, composer, JsonGrammarHelper.FindRootObjectNode(ast));

        Assert.True(JsonGrammarHelper.TryComposedProperty(ctx, "word", out var wordId));
        Assert.Equal(ContentTierSpine.ResolveRoot(surface), wordId);
    }
}
