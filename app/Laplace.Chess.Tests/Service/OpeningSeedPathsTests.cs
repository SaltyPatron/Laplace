using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class OpeningSeedPathsTests
{
    private static void WriteCatalog(string dir, string skip = "")
    {
        Directory.CreateDirectory(dir);
        foreach (var letter in "abcde")
            if (!skip.Contains(letter))
                File.WriteAllText(Path.Combine(dir, letter + ".tsv"), "eco\tname\tpgn\n");
    }

    [Fact]
    public void DefaultUsesCompleteRefreshedCheckoutWithLegacyCorpusFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), $"opening-paths-{Guid.NewGuid():N}");
        var refreshed = Path.Combine(root, "lichess-openings");
        var legacy = Path.Combine(root, "openings");
        Directory.CreateDirectory(refreshed);
        Directory.CreateDirectory(legacy);
        try
        {
            Assert.Equal(refreshed, OpeningSeed.ResolveDefaultDir(root));
            WriteCatalog(legacy);
            Assert.Equal(legacy, OpeningSeed.ResolveDefaultDir(root));
            WriteCatalog(refreshed, skip: "c");
            Assert.Equal(legacy, OpeningSeed.ResolveDefaultDir(root));
            WriteCatalog(refreshed);
            Assert.Equal(refreshed, OpeningSeed.ResolveDefaultDir(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DataRootAndExplicitCorpusOverrideAreHonored()
    {
        var priorData = Environment.GetEnvironmentVariable("LAPLACE_WORK");
        var priorCorpus = Environment.GetEnvironmentVariable("LAPLACE_DATA");
        var priorOpenings = Environment.GetEnvironmentVariable("LAPLACE_CHESS_OPENINGS");
        var priorPrefix = Environment.GetEnvironmentVariable("LAPLACE_INSTALL_PREFIX");
        string root = Path.Combine(Path.GetTempPath(), $"opening-root-{Guid.NewGuid():N}");
        string refreshed = Path.Combine(root, "Games", "Chess", "lichess-openings");
        WriteCatalog(refreshed);
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_INSTALL_PREFIX", root);
            Environment.SetEnvironmentVariable("LAPLACE_DATA", root);
            Environment.SetEnvironmentVariable("LAPLACE_WORK", root);
            Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", null);
            Assert.Equal(refreshed, OpeningSeed.DefaultDir);
            Environment.SetEnvironmentVariable("LAPLACE_WORK", Path.Combine(root, "service-state"));
            Environment.SetEnvironmentVariable("LAPLACE_DATA", root);
            Assert.Equal(refreshed, OpeningSeed.DefaultDir);
            string explicitPath = Path.Combine(root, "operator-selected");
            Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", explicitPath);
            Assert.Equal(explicitPath, OpeningSeed.DefaultDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_WORK", priorData);
            Environment.SetEnvironmentVariable("LAPLACE_DATA", priorCorpus);
            Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", priorOpenings);
            Environment.SetEnvironmentVariable("LAPLACE_INSTALL_PREFIX", priorPrefix);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NoCompleteCatalogIsAnErrorNamingWhereItLooked()
    {
        var priorCorpus = Environment.GetEnvironmentVariable("LAPLACE_DATA");
        var priorOpenings = Environment.GetEnvironmentVariable("LAPLACE_CHESS_OPENINGS");
        var priorPrefix = Environment.GetEnvironmentVariable("LAPLACE_INSTALL_PREFIX");
        string root = Path.Combine(Path.GetTempPath(), $"opening-none-{Guid.NewGuid():N}");
        WriteCatalog(Path.Combine(root, "Games", "Chess", "lichess-openings"), skip: "e");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_INSTALL_PREFIX", root);
            Environment.SetEnvironmentVariable("LAPLACE_DATA", root);
            Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", null);
            var failure = Assert.Throws<DirectoryNotFoundException>(() => OpeningSeed.DefaultDir);
            Assert.Contains(Path.Combine(root, "Games", "Chess", "lichess-openings"), failure.Message, StringComparison.Ordinal);
            Assert.Contains(Path.Combine(root, "share", "laplace", "chess-openings"), failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_DATA", priorCorpus);
            Environment.SetEnvironmentVariable("LAPLACE_CHESS_OPENINGS", priorOpenings);
            Environment.SetEnvironmentVariable("LAPLACE_INSTALL_PREFIX", priorPrefix);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogIsAThroughEAndAPartialOrMissingCatalogThrowsWithItsPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace-openings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.tsv"), "eco\tname\tpgn\nA00\tAlpha\t1. a3\n");
            File.WriteAllText(Path.Combine(dir, "e.tsv"), "eco\tname\tpgn\nE00\tEcho\t1. d4 Nf6\n");
            File.WriteAllText(Path.Combine(dir, "z.tsv"), "eco\tname\tpgn\nZ00\tIgnored\t1. h4\n");
            var partial = Assert.Throws<FileNotFoundException>(() => OpeningSeed.Lines(dir));
            Assert.Contains(dir, partial.Message, StringComparison.Ordinal);
            Assert.Contains("b.tsv, c.tsv, d.tsv", partial.Message, StringComparison.Ordinal);
            Assert.Throws<FileNotFoundException>(() => OpeningSeed.Fens(dir));

            foreach (var letter in "bcd") File.WriteAllText(Path.Combine(dir, letter + ".tsv"), "eco\tname\tpgn\n");
            Assert.Equal(new[] { "Alpha", "Echo" }, OpeningSeed.Lines(dir).Select(line => line.Name));

            string missing = Path.Combine(dir, "absent");
            var absent = Assert.Throws<DirectoryNotFoundException>(() => OpeningSeed.Lines(missing));
            Assert.Contains(missing, absent.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
