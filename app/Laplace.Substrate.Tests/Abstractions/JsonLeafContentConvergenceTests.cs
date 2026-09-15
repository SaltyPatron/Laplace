using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class JsonLeafContentConvergenceTests
{
    private static readonly Hash128 Src =
        SubstrateCanonicalIds.OfVersioned("source", "test", "json-leaf-convergence");

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

    [Theory]
    [InlineData("café", "caf\\u00e9")]
    [InlineData("🚀", "\\uD83D\\uDE80")]
    [InlineData("café 🚀 棋", "caf\\u00e9 \\uD83D\\uDE80 棋")]
    public void JsonStringLeaf_EscapedUnicode_ConvergesWith_ContentPath(string surface, string escaped)
    {
        string doc = "{\"w\":\"" + escaped + "\"}";
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

public sealed class JsonStringDecodingTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("café 棋", "café 棋")]
    [InlineData("caf\\u00E9", "café")]
    [InlineData("\\uD800\\uDC00", "𐀀")]
    [InlineData("\\ud83d\\ude80", "🚀")]
    [InlineData("\\uDBFF\\uDFFF", "􏿿")]
    [InlineData("café \\uD83D\\uDE80 \\u68CB", "café 🚀 棋")]
    [InlineData("\\\"\\\\\\/\\b\\f\\n\\r\\t\\u0000", "\"\\/\b\f\n\r\t\0")]
    public void EscapedAndLiteralUnicodeHaveIdenticalDecodedBytes(string inner, string expected)
    {
        byte[] literal = Encoding.UTF8.GetBytes(expected);
        byte[] escaped = Encoding.UTF8.GetBytes(inner);
        Assert.Equal(literal, GrammarDecomposer.DecodeJsonStringUtf8(escaped));
        Assert.Equal(expected, JsonGrammarHelper.Utf8ToString(Encoding.UTF8.GetBytes("\"" + inner + "\"")));
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\uD800x")]
    [InlineData("\\uD800\\uD800")]
    [InlineData("\\uD800\\u0041")]
    [InlineData("\\uDC00\\uD800")]
    [InlineData("\\uD800\\uDC0")]
    [InlineData("\\u12")]
    [InlineData("\\uZZZZ")]
    [InlineData("\\x")]
    [InlineData("\\")]
    public void MalformedEscapesCannotBecomeDifferentContent(string inner)
    {
        Assert.Throws<InvalidDataException>(() =>
            GrammarDecomposer.DecodeJsonStringUtf8(Encoding.UTF8.GetBytes(inner)));
        Assert.Throws<InvalidDataException>(() =>
            JsonGrammarHelper.Utf8ToString(Encoding.UTF8.GetBytes("\"" + inner + "\"")));
    }

    [Theory]
    [InlineData("EDA080")]
    [InlineData("C080")]
    [InlineData("F4908080")]
    [InlineData("E282")]
    [InlineData("00")]
    [InlineData("22")]
    public void InvalidLiteralStringBytesAreRejected(string hex)
    {
        Assert.Throws<InvalidDataException>(() =>
            GrammarDecomposer.DecodeJsonStringUtf8(Convert.FromHexString(hex)));
    }
}
