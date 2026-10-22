using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class NativeRecipeExecutionTests
{
    [Theory]
    [InlineData(NativeRecipeMode.Plan)]
    [InlineData(NativeRecipeMode.NoLoad)]
    [InlineData(NativeRecipeMode.Claims)]
    public async Task CanonicalEngineReadsRealPropbankThroughTheManagedProcessBoundary(NativeRecipeMode mode)
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out var repository));
        string workspace = Directory.GetParent(repository)!.FullName;
        var execution = NativeRecipeExecution.Configured();
        string fixture = Path.Combine(workspace, "Laplace-Engine", "tests", "fixtures",
            "highway-identities", "propbank", "abandon.xml");
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await execution.ExecuteAsync(new NativeRecipeRequest(mode, "propbank", [fixture],
            ConnectionInfo: "host=127.0.0.1 port=1 dbname=unavailable connect_timeout=1", Threads: 2), output, error);
        string stdout = Encoding.UTF8.GetString(output.ToArray());
        string stderr = Encoding.UTF8.GetString(error.ToArray());
        Assert.True(code == 0, $"Native mode {mode} exited {code}: {stderr}\n{stdout}");
        if (mode == NativeRecipeMode.Claims)
        {
            // These are three different attribute-bearing rolesets from the real
            // file, recovered by the same compiled Engine recipe used for loading.
            Assert.Contains("abandon.01", stdout, StringComparison.Ordinal);
            Assert.Contains("abandon.02", stdout, StringComparison.Ordinal);
            Assert.Contains("abandon.03", stdout, StringComparison.Ordinal);
            Assert.Contains("leave behind", stdout, StringComparison.Ordinal);
        }
        else if (mode == NativeRecipeMode.Plan)
            Assert.Matches(@"propbank-frames\s+1 files", stdout);
        else
        {
            Assert.Contains("mismatched 0", stdout, StringComparison.Ordinal);
            Assert.Matches(@"roleset\.name\s+3(?:\r?\n|$)", stdout);
            Assert.Contains("unresolved obligations: 0", stdout, StringComparison.Ordinal);
        }
    }
}
