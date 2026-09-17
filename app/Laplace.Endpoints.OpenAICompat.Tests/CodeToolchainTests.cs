using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class CodeToolchainTests
{
    [Fact]
    public async Task JsonVerificationProducesTreeAndToolchainReceipts()
    {
        const string valid = "{\"value\":1}";
        using var validAst = GrammarDecomposer.Parse(Encoding.UTF8.GetBytes(valid), "json");
        var accepted = await CodeToolchain.VerifyAsync(
            valid, "json", validAst.Diagnostics, CancellationToken.None);
        Assert.True(accepted.ToolAvailable);
        Assert.True(accepted.SyntaxComplete);
        Assert.True(accepted.Verified);
        Assert.Equal(0, accepted.ExitCode);
        Assert.Contains("laplace.code-toolchain/v2", accepted.CanonicalJson, StringComparison.Ordinal);

        const string invalid = "{\"value\":}";
        using var invalidAst = GrammarDecomposer.Parse(Encoding.UTF8.GetBytes(invalid), "json");
        var rejected = await CodeToolchain.VerifyAsync(
            invalid, "json", invalidAst.Diagnostics, CancellationToken.None);
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
    public void CodeModelHasAnExactGovernedIdWithoutPrematureCatalogPromotion()
    {
        Assert.True(ModelCatalog.IsCode("laplace-code-001"));
        Assert.DoesNotContain(ModelCatalog.All, model => model.Id == ModelCatalog.Code);
    }
}
