using System.Runtime.CompilerServices;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class UserContentConnectionConcurrencyGateTests
{
    [Fact]
    public void Export_ReleasesMembershipConnectionAndOverlapsObservationWithReconstruction()
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/SubstrateClient.UserContent.cs");

        Assert.Contains("await using (var conn = await _dataSource.OpenConnectionAsync", text);
        Assert.Contains("var contentTask = NpgsqlContentReconstructor.ReconstructUtf8Async", text);
        Assert.Contains("var observationTask = NpgsqlSubstrateReads.UserArtifactObservationAsync", text);
        Assert.Contains("await Task.WhenAll(contentTask, observationTask)", text);
        Assert.Contains("_dataSource, userArtifacts.SourceName, requested, ct", text);
        Assert.DoesNotContain("UserArtifactObservationAsync(\n                conn, userArtifacts.SourceName", text);
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
