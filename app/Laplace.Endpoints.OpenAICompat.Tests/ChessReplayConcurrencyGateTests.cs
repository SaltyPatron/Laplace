using System.Runtime.CompilerServices;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

public sealed class ChessReplayConcurrencyGateTests
{
    [Fact]
    public void TypedReplay_OverlapsTrajectoryAndSetupLookupAfterLineResolution()
    {
        var text = Read("app/Laplace.Endpoints.OpenAICompat/SubstrateClient.Chess.cs");
        var start = text.IndexOf("private async Task<Laplace.Chess.Service.ChessReplayResult> ChessTypedReplayAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var method = text[start..];

        Assert.Contains("var rowsTask = NpgsqlSubstrateReads.TypedTrajectoryConstituentsAsync", method);
        Assert.Contains("var setupIdTask = NpgsqlSubstrateReads.ChessSetupPositionIdAsync", method);
        Assert.Contains("await Task.WhenAll(rowsTask, setupIdTask)", method);
        Assert.DoesNotContain("var rows = await NpgsqlSubstrateReads.TypedTrajectoryConstituentsAsync", method);
        Assert.DoesNotContain("byte[]? setupId = await NpgsqlSubstrateReads.ChessSetupPositionIdAsync", method);
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
