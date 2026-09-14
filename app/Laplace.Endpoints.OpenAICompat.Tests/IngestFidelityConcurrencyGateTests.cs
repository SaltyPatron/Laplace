using System.Runtime.CompilerServices;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class IngestFidelityConcurrencyGateTests
{
    [Fact]
    public void PositiveAndNegativePopulations_AreStartedBeforeEitherIsAwaited()
    {
        var text = Read("app/Laplace.Cli/EvalCommands.cs");
        Assert.Contains("var posTask = NpgsqlSubstrateReads.IngestFidelityPositiveScoresAsync", text);
        Assert.Contains("var negTask = NpgsqlSubstrateReads.IngestFidelityNegativeScoresAsync", text);
        Assert.Contains("await Task.WhenAll(posTask, negTask)", text);
        Assert.DoesNotContain("var pos = (await NpgsqlSubstrateReads.IngestFidelityPositiveScoresAsync", text);
        Assert.DoesNotContain("var neg = (await NpgsqlSubstrateReads.IngestFidelityNegativeScoresAsync", text);
    }

    private static string Read(string repoRelative) => File.ReadAllText(RepoPath(repoRelative));

    private static string RepoPath(string repoRelative) =>
        Path.Combine(RepoRoot(), repoRelative.Replace('/', Path.DirectorySeparatorChar));

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null &&
               !(Directory.Exists(Path.Combine(dir.FullName, "docs"))
                 && Directory.Exists(Path.Combine(dir.FullName, "app"))))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("repo root not found above test source");
    }
}
