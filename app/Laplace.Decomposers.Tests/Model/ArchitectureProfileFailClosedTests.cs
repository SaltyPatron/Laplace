using Xunit;

namespace Laplace.Decomposers.Model.Tests;

public sealed class ArchitectureProfileFailClosedTests
{
    [Theory]
    [InlineData("qwen3")]
    [InlineData("qwen3_moe")]
    [InlineData("florence2")]
    [InlineData("conditional_detr")]
    public void For_rejects_unsupported_model_type(string modelType)
    {
        var ex = Assert.Throws<NotSupportedException>(() => ArchitectureProfile.For(modelType));

        Assert.Contains(modelType, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Unsupported model_type", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("llama", "llama")]
    [InlineData("PHI", "phi")]
    [InlineData(" qwen2 ", "qwen2")]
    [InlineData("BERT", "bert")]
    public void For_keeps_supported_profiles_and_normalizes_input(string modelType, string expected)
    {
        Assert.Equal(expected, ArchitectureProfile.For(modelType).ModelType);
    }
}
