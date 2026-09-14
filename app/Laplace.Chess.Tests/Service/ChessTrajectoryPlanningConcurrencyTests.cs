using System.Runtime.CompilerServices;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessTrajectoryPlanningConcurrencyTests
{
    [Fact]
    public void IndependentPlanningCounts_AreStartedBeforeEitherIsAwaited()
    {
        var text = File.ReadAllText(RepoPath("app/Laplace.Chess/Service/ChessTrajectoryDecomposer.cs"));
        var start = text.IndexOf("public override async Task<long?> EstimateUnitCountAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var method = text[start..];

        Assert.Contains("var linesTask = ChessWitnessHydrator.CountRecordedLinesAsync", method);
        Assert.Contains("var playersTask = NpgsqlSubstrateReads.CountChessPlayersMissingPhysicalityAsync", method);
        Assert.Contains("await Task.WhenAll(linesTask, playersTask)", method);
        Assert.DoesNotContain("long lines = await ChessWitnessHydrator.CountRecordedLinesAsync", method);
        Assert.DoesNotContain("long players = await NpgsqlSubstrateReads.CountChessPlayersMissingPhysicalityAsync", method);
    }

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
