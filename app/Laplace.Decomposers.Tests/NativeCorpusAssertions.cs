using System.Text;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Tests;

internal static class NativeCorpusAssertions
{
    public static async Task<string[]> ClaimsAsync(string source, params string[] paths)
    {
        Assert.All(paths, static path => Assert.True(File.Exists(path), path));
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await NativeRecipeExecution.Configured().ExecuteAsync(
            new NativeRecipeRequest(NativeRecipeMode.Claims, source, paths, Threads: 4), output, error);
        string stdout = Encoding.UTF8.GetString(output.ToArray());
        Assert.True(code == 0, Encoding.UTF8.GetString(error.ToArray()) + stdout);
        Assert.DoesNotMatch(@"unresolved obligations: [1-9]", stdout);
        return stdout.Split('\n').Select(static line => line.TrimEnd('\r'))
            .Where(static line => line.StartsWith("claim\t", StringComparison.Ordinal)).ToArray();
    }
}
