using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessExploreReadPathTests
{
    [Fact]
    public void ExploreUsesKeyedMoveCells_NotWholeGameTrajectoryScans()
    {
        string root = FindRepoRoot();
        string service = File.ReadAllText(Path.Combine(
            root, "app", "Laplace.Chess", "Service", "ChessEngineService.cs"));
        string reads = File.ReadAllText(Path.Combine(
            root, "app", "Laplace.Substrate", "Crud", "Npgsql", "NpgsqlSubstrateReads.cs"));

        Assert.Contains("ChessMovesAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ChessTrajectorySuccessorsAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("chess_trajectory_successors", reads, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Laplace.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Laplace repository root not found");
    }
}
