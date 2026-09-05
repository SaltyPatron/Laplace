using Laplace.Endpoints.OpenAICompat;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

/// <summary>
/// The HTTP export service binds the GGUF synthesis command. Its advertised formats
/// must reflect complete route integration, even when another native codec exists.
/// </summary>
public sealed class FoundryExportFormatTests
{
    [Fact]
    public void OnlyFormatsWithAnIntegratedExportPathAreWritable()
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
