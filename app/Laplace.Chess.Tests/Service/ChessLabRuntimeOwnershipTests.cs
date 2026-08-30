using Xunit;

namespace Laplace.Chess.Tests.Service;

public sealed class ChessLabRuntimeOwnershipTests
{
    [Fact]
    public void LabRunners_BorrowHostOwnedRuntimeInsteadOfCreatingPools()
    {
        var root = FindRepoRoot();
        var runners = File.ReadAllText(Path.Combine(
            root, "app", "Laplace.Chess", "Service", "ChessLabRunners.cs"));

        Assert.DoesNotContain("LaplaceDataSource.Create", runners, StringComparison.Ordinal);
        Assert.DoesNotContain("ChessLiveGameHost.CreateAsync", runners, StringComparison.Ordinal);
        Assert.DoesNotContain("ChessLabRecorder.OpenAsync", runners, StringComparison.Ordinal);
        Assert.Contains("lab.GetLiveHostAsync", runners, StringComparison.Ordinal);
        Assert.Contains("ChessPgnIngestor.AttachAsync", runners, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiComposition_InjectsGenericHostOwnedChessRuntimeIntoLab()
    {
        var root = FindRepoRoot();
        var composition = File.ReadAllText(Path.Combine(
            root, "app", "Laplace.Endpoints.OpenAICompat", "AppComposition.cs"));

        Assert.Contains(
            "sp.GetRequiredService<ChessRuntimeService>().GetAsync",
            composition,
            StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CMakeLists.txt"))
                && Directory.Exists(Path.Combine(dir.FullName, "app", "Laplace.Chess")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("could not locate Laplace repository root");
    }
}
