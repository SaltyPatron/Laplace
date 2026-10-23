using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.OMW;

[Trait("Category", "RequiresQualification")]
public sealed class OMWLmfCoverageAuditTests
{
    [SkippableFact]
    public async Task All32CurrentLexiconsUseTheNativeRecipeWithoutUnresolvedFieldsOrDecompositionMismatches()
    {
        string? data = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_DATA");
        Skip.If(string.IsNullOrWhiteSpace(data), "LAPLACE_DATA must declare the existing corpus estate.");
        string root = Path.Combine(data!, "OMW-2.0", "omw-2.0");
        Skip.IfNot(Directory.Exists(root), "staged OMW 2.0 estate is not mounted");
        string[] files = Directory.GetFiles(root, "omw-*.xml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(32, files.Length);

        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await NativeRecipeExecution.Configured().ExecuteAsync(
            new NativeRecipeRequest(NativeRecipeMode.NoLoad, "open-multilingual-wordnet", files, Threads: 4),
            output, error);
        string text = Encoding.UTF8.GetString(output.ToArray()) + Encoding.UTF8.GetString(error.ToArray());
        Assert.True(code == 0, text);
        Assert.Contains("== decomposition: 32 files", text);
        Assert.DoesNotMatch(@"unresolved obligations: [1-9]", text);
        Assert.DoesNotMatch(@"mismatched [1-9]", text);
    }
}
