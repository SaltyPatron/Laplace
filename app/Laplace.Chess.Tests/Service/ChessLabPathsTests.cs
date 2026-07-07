using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessLabPathsTests
{
    [Fact]
    public void Cutechess_UsesExplicitPathWhenProvided()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"cutechess-{Guid.NewGuid():N}.exe");
        File.WriteAllText(fake, "");

        var probe = ChessLabPaths.ResolveExecutableForTest(
            fake,
            _ => null,
            ["cutechess-cli.exe"]);

        Assert.Equal("config", probe.Source);
        Assert.Equal(fake, probe.Path);
        Assert.True(probe.Found);
    }

    [Fact]
    public void Stockfish_UsesRepoRelativeWhenAvailable()
    {
        if (!LaplaceInstall.TryRepoRoot(out var root))
        {
            return;
        }

        var sf = Path.Combine(root, "build-cutechess", "stockfish.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(sf)!);
        File.WriteAllText(sf, "");

        var probe = ChessLabPaths.ResolveExecutableForTest(
            null,
            r => Path.Combine(r, "build-cutechess", "stockfish.exe"),
            ["stockfish.exe"]);

        Assert.Equal("repo", probe.Source);
        Assert.Equal(sf, probe.Path);
        Assert.True(probe.Found);
    }

    [Fact]
    public void LaplaceUci_DiscoversOnPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"laplace-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var uci = Path.Combine(dir, "laplace-uci.exe");
        File.WriteAllText(uci, "");

        var probe = ChessLabPaths.ResolveExecutableForTest(
            uci,
            _ => null,
            ["laplace-uci.exe", "laplace-uci"]);

        Assert.Equal("config", probe.Source);
        Assert.Equal(uci, probe.Path);
        Assert.True(probe.Found);
    }

    [Fact]
    public void QtBin_MissingWhenUnset()
    {
        var probe = ChessLabPaths.ResolveQtBinForTest(null);

        Assert.Equal("missing", probe.Source);
        Assert.False(probe.Found);
        Assert.Null(probe.Path);
    }

    [Fact]
    public void QtBin_UsesConfigPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qt-bin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var probe = ChessLabPaths.ResolveQtBinForTest(dir);

        Assert.Equal("config", probe.Source);
        Assert.Equal(dir, probe.Path);
        Assert.True(probe.Found);
    }

    [Fact]
    public void LaplaceUci_FallsBackWhenConfigPathMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"laplace-neighbor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var uci = Path.Combine(dir, "laplace-uci.exe");
        File.WriteAllText(uci, "");

        var probe = ChessLabPaths.ResolveExecutableForTest(
            Path.Combine(Path.GetTempPath(), $"missing-uci-{Guid.NewGuid():N}.exe"),
            _ => null,
            ["laplace-uci.exe", "laplace-uci"],
            uci);

        Assert.Equal("path", probe.Source);
        Assert.Equal(uci, probe.Path);
        Assert.True(probe.Found);
    }

    [Fact]
    public void LabDir_UsesTempRoot()
    {
        Assert.StartsWith(Path.GetTempPath(), ChessLabPaths.LabDir, StringComparison.OrdinalIgnoreCase);
    }
}
