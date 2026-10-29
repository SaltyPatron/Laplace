using System.Text;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class SemLinkIngestPipelineTests
{
    private const string Mapping =
        """{"abandon.01":{"51.2":{"ARG0":"theme"}},"abandon.02":{"51.2":{"ARG0":"theme"}}}""";

    private static readonly string[] Expected =
    [
        "claim\t0\t1\tabandon.01\tvnclass:leave-51.2",
        "claim\t0\t1\t[abandon.01, vnclass:leave-51.2]\tARG0\ttheme",
        "claim\t0\t1\tabandon.02\tvnclass:leave-51.2",
        "claim\t0\t1\t[abandon.02, vnclass:leave-51.2]\tARG0\ttheme",
        "claim\t0\t1\tabandon.01\t[pbroleset, cross-resource mapping, vnclass]\t51.2",
        "claim\t0\t1\tabandon.02\t[pbroleset, cross-resource mapping, vnclass]\t51.2",
        "claim\t0\t1\tabandon.01\tidentifier vocabulary\tpbroleset",
        "claim\t0\t1\tabandon.02\tidentifier vocabulary\tpbroleset",
        "claim\t0\t1\tvnclass:leave-51.2\tidentifier vocabulary\tvnclass",
    ];

    [Fact]
    public async Task NativePlanSelectsTheDeclaredMappingRecipe()
    {
        await WithInput(async path =>
        {
            string output = await Execute(NativeRecipeMode.Plan, path, 1);
            Assert.Contains("semlink-pb-vn", output, StringComparison.Ordinal);
            Assert.Contains("1 files", output, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task NativeMappingPreservesRolesetClassAndRoleBindings(int workers)
    {
        await WithInput(async path =>
        {
            string output = await Execute(NativeRecipeMode.Claims, path, workers);
            Assert.Contains("unresolved obligations: 0", output, StringComparison.Ordinal);
            string[] claims = output.Split('\n').Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("claim\t", StringComparison.Ordinal)).ToArray();
            Assert.Equal(Expected.Order(StringComparer.Ordinal), claims.Order(StringComparer.Ordinal));
        });
    }

    private static async Task<string> Execute(NativeRecipeMode mode, string path, int workers)
    {
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await NativeRecipeExecution.Configured().ExecuteAsync(
            new NativeRecipeRequest(mode, "semlink", [path], Threads: workers), output, error);
        Assert.True(code == 0, Encoding.UTF8.GetString(error.ToArray()) + Encoding.UTF8.GetString(output.ToArray()));
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task WithInput(Func<string, Task> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "semlink-native-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "pb-vn2.json");
        try { await File.WriteAllTextAsync(path, Mapping); await check(path); }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}
