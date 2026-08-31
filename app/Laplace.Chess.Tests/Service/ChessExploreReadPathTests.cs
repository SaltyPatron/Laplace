using Laplace.Engine.Core;
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
        if (LaplaceInstall.TryRepoRoot(out string root)) return root;
        throw new DirectoryNotFoundException("Laplace repository root not found");
    }
}
