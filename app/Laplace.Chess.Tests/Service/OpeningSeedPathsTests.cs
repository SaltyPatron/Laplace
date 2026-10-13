using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class OpeningSeedPathsTests
{
    [Fact]
    public void DefaultUsesPopulatedRefreshedCheckoutWithLegacyCorpusFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), $"opening-paths-{Guid.NewGuid():N}");
        var refreshed = Path.Combine(root, "lichess-openings");
        var legacy = Path.Combine(root, "openings");
        Directory.CreateDirectory(refreshed);
        Directory.CreateDirectory(legacy);
        try
        {
            Assert.Equal(refreshed, OpeningSeed.ResolveDefaultDir(root));
            File.WriteAllText(Path.Combine(legacy, "a.tsv"), "eco\tname\tpgn\n");
            Assert.Equal(legacy, OpeningSeed.ResolveDefaultDir(root));
            File.WriteAllText(Path.Combine(refreshed, "a.tsv"), "eco\tname\tpgn\n");
            Assert.Equal(refreshed, OpeningSeed.ResolveDefaultDir(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DataRootAndExplicitCorpusOverrideAreHonored()
    {
        var priorData = Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT");
        var priorCorpus = Environment.GetEnvironmentVariable("LAPLACE_DATA");
        var priorOpenings = Environment.GetEnvironmentVariable("LAPLACE_CHESS_OPENINGS");
        var priorPrefix = Environment.GetEnvironmentVariable("LAPLACE_INSTALL_PREFIX");
        string root = Path.Combine(Path.GetTempPath(), $"opening-root-{Guid.NewGuid():N}");
        string refreshed = Path.Combine(root, "Games", "Chess", "lichess-openings");
        Directory.CreateDirectory(refreshed);
        File.WriteAllText(Path.Combine(refreshed, "a.tsv"), "eco\tname\tpgn\n");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_INSTALL_PREFIX", root);
            Environment.SetEnvironmentVariable("LAPLACE_DATA", null);
            Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", root);
            Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", null);
            Assert.Equal(refreshed, OpeningSeed.DefaultDir);
            Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", Path.Combine(root, "service-state"));
            Environment.SetEnvironmentVariable("LAPLACE_DATA", root);
            Assert.Equal(refreshed, OpeningSeed.DefaultDir);
            string explicitPath = Path.Combine(root, "operator-selected");
            Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", explicitPath);
            Assert.Equal(explicitPath, OpeningSeed.DefaultDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", priorData);
            Environment.SetEnvironmentVariable("LAPLACE_DATA", priorCorpus);
            Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", priorOpenings);
            Environment.SetEnvironmentVariable("LAPLACE_INSTALL_PREFIX", priorPrefix);
            Directory.Delete(root, recursive: true);
        }
    }
}
