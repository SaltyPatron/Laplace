using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Tests.Structured.SelectedRecipeSemanticsTests;

namespace Laplace.Decomposers.Tests.Structured;

public sealed class UnicodeSourceSemanticsTests
{
    private const string Recipes = "recipes/unicode/UCD/17.0.0/";

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public void Link_formatting_preserves_the_pair_and_url_fragments(int chunk)
    {
        var rows = Read(Recipes + "link-formatting-test.recipe.json",
            "# comment\nhttps://example.com/#%CE%B1\n\n# between pair\nhttps://example.com/#α\n", chunk);
        AssertClaim(rows, "https://example.com/#%CE%B1", "NORMALIZES_TO", "https://example.com/#α", "UTS58/LinkFormatting");
    }

    [Fact]
    public void Link_formatting_rejects_an_incomplete_pair()
    {
        var error = Assert.Throws<InvalidDataException>(() => Read(Recipes + "link-formatting-test.recipe.json", "https://example.com/%CE%B1\n"));
        Assert.Contains("incomplete multiline record", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public void Link_detection_preserves_repeated_spans_codepoint_offsets_and_zero_link_examples(int chunk)
    {
        var rows = Read(Recipes + "link-detection-test.recipe.json",
            "é ⸠a.com#x⸡ ⸠a.com#x⸡\nno link here\n", chunk);
        const string text = "é a.com#x a.com#x";
        Hash128 first = Hash128.Merkle(EntityTier.Document, [Root(text), Root("2"), Root("8")]);
        Hash128 second = Hash128.Merkle(EntityTier.Document, [Root(text), Root("10"), Root("16")]);
        foreach (var span in new[] { first, second })
        {
            Assert.Contains(rows, r => r.SubjectId == Root(text) && r.ObjectId == span
                && r.TypeId == RelationTypeRegistry.RelationTypeId("HAS_PARSE"));
            Assert.Contains(rows, r => r.SubjectId == span && r.ObjectId == Root("a.com#x")
                && r.ContextId == Root("Link_Text"));
        }
        AssertClaim(rows, text, "HAS_ATTRIBUTE", "2", "Link_Count");
        AssertClaim(rows, "no link here", "HAS_ATTRIBUTE", "0", "Link_Count");
    }

    [Theory]
    [InlineData("⸠unterminated")]
    [InlineData("unopened⸡")]
    [InlineData("⸠nested ⸠link⸡⸡")]
    public void Malformed_link_annotations_are_rejected(string source) =>
        Assert.Throws<InvalidDataException>(() => Read(Recipes + "link-detection-test.recipe.json", source));

    [Fact]
    public void Link_properties_resolve_codepoints_and_keep_property_names()
    {
        var bracket = Read(Recipes + "link-bracket.recipe.json", "0029 ; 0028 # closing to opening\n");
        Hash128 bracketProperty = Hash128.Merkle(EntityTier.Document, [Root("Link_Bracket"), Root("(")]);
        Assert.Contains(bracket, r => r.SubjectId == Root(")") && r.ObjectId == bracketProperty
            && r.TypeId == RelationTypeRegistry.RelationTypeId("HAS_CHARACTER_PROPERTY"));
        var email = Read(Recipes + "link-email.recipe.json", "0041..0042 # range\n");
        Hash128 emailProperty = Hash128.Merkle(EntityTier.Document, [Root("Link_Email"), Root("Yes")]);
        Assert.Contains(email, r => r.SubjectId == Root("A") && r.ObjectId == emailProperty);
        Assert.Contains(email, r => r.SubjectId == Root("B") && r.ObjectId == emailProperty);
        var term = Read(Recipes + "link-term.recipe.json", "0021 ; Soft\n");
        Hash128 termProperty = Hash128.Merkle(EntityTier.Document, [Root("Link_Term"), Root("Soft")]);
        Assert.Contains(term, r => r.SubjectId == Root("!") && r.ObjectId == termProperty);
    }
}
