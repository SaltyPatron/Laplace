using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class JsonAstSerializationTests
{
    [Fact]
    public void GrammarValuesRetainNumberBitsAndEscapedTextWhenEmbeddedInAReceipt()
    {
        const string source = "{\"integer\":18446744073709551615,\"decimal\":0.12345678901234567890123456789,\"text\":\"\\u732b\\n\\\"\"}";
        using var document = JsonAstDocument.Parse(source);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("recipe");
            document.Root.WriteTo(writer);
            writer.WriteEndObject();
        }
        Assert.Equal("{\"recipe\":" + source + "}", Encoding.UTF8.GetString(buffer.ToArray()));
    }

    [Theory]
    [InlineData("{\"truncated\":")]
    [InlineData("{} {}")]
    [InlineData("{} trailing")]
    [InlineData("{/* comment */\"value\":1}")]
    public void ReceiptParsingRejectsIncompleteOrAdditionalSource(string source)
    {
        Assert.Throws<InvalidDataException>(() => JsonAstDocument.Parse(source));
    }
}
