using Laplace.Endpoints.OpenAICompat;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// The export service always runs `synthesize substrate`, the GGUF writer. `format` used to
/// select only the filename EXTENSION, so format="safetensors" produced GGUF bytes in a file
/// named .safetensors and returned Format="safetensors" — an artifact that lies about what it
/// is. There is no SafeTensors export anywhere in the tree; SafeTensors appears only as an
/// ingest witness. Refusing is the only honest answer until a writer exists.
/// </summary>
public sealed class FoundryExportFormatTests
{
    [Fact]
    public void OnlyFormatsWithAWriterAreWritable()
        => Assert.Equal(new[] { "gguf" }, CliFoundryExportService.WritableFormats.Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("safetensors")]
    [InlineData("safetensor")]
    [InlineData("onnx")]
    [InlineData("pt")]
    public async Task UnwritableFormat_IsRefused_NotSilentlyMislabelled(string format)
    {
        var svc = new CliFoundryExportService();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => svc.ExportAsync(
            recipeJson: null, recipeIdPrefix: "deadbeef", tokenizerDir: null,
            format: format, filename: null, ct: default));
        Assert.Contains(format, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gguf", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
