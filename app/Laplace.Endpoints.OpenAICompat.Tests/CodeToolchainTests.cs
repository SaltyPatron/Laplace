using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class CodeToolchainTests
{
    [Fact]
    public async Task JsonVerificationProducesPassAndFailureReceipts()
    {
        var accepted = await CodeToolchain.VerifyAsync("{\"value\":1}", "json", CancellationToken.None);
        Assert.True(accepted.ToolAvailable);
        Assert.True(accepted.Verified);
        Assert.Equal(0, accepted.ExitCode);
        Assert.Contains("laplace.code-toolchain/v1", accepted.CanonicalJson, StringComparison.Ordinal);

        var rejected = await CodeToolchain.VerifyAsync("{\"value\":}", "json", CancellationToken.None);
        Assert.True(rejected.ToolAvailable);
        Assert.False(rejected.Verified);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.NotEmpty(rejected.Stderr);
    }

    [Theory]
    [InlineData("py", "python")]
    [InlineData("c#", "c-sharp")]
    [InlineData("rs", "rust")]
    [InlineData("ts", "typescript")]
    public void CodeModelUsesNativeGrammarRegistry(string requested, string expected)
    {
        Assert.True(CodePlayerService.TryNormalizeModality(requested, out var modality));
        Assert.Equal(expected, modality);
    }

    [Fact]
    public void CodeModelIsAdvertisedOnlyAfterOwningARealEndpoint()
    {
        Assert.Contains(ModelCatalog.All, model => model.Id == ModelCatalog.Code);
    }
}
