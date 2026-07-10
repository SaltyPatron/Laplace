using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessLabPathsTests
{
    [Fact]
    public void Cutechess_UsesConfigPathWhenProvided()
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
    public void Stockfish_UsesExternalBuildRootWhenAvailable()
    {
        var buildRoot = Path.Combine(Path.GetTempPath(), $"laplace-build-{Guid.NewGuid():N}");
        var cutechessBuild = Path.Combine(buildRoot, "build-cutechess");
        Directory.CreateDirectory(cutechessBuild);
        var sf = Path.Combine(cutechessBuild, "stockfish.exe");
        File.WriteAllText(sf, "");
        var prior = Environment.GetEnvironmentVariable("LAPLACE_CUTECHESS_BUILD");
        Environment.SetEnvironmentVariable("LAPLACE_CUTECHESS_BUILD", cutechessBuild);
        try
        {
            var probe = ChessLabPaths.ResolveExecutableForTest(
                null,
                _ => Path.Combine(cutechessBuild, "stockfish.exe"),
                ["stockfish.exe"]);

            Assert.Equal("build", probe.Source);
            Assert.Equal(sf, probe.Path);
            Assert.True(probe.Found);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_CUTECHESS_BUILD", prior);
            Directory.Delete(buildRoot, recursive: true);
        }
    }

    [Fact]
    public void LaplaceUci_UsesInstallRootContract()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"laplace-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var uci = Path.Combine(dir, "laplace-uci.exe");
        File.WriteAllText(uci, "");

        var probe = ChessLabPaths.ResolveLaplaceUciForTest(uci);

        Assert.Equal("install", probe.Source);
        Assert.Equal(uci, probe.Path);
        Assert.True(probe.Found);
    }

    [Fact]
    public void LaplaceUci_IgnoresStaleConfig_InstallRootIsCanonical()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"laplace-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var uci = Path.Combine(dir, "laplace-uci.exe");
        File.WriteAllText(uci, "");

        // ResolveLaplaceUci has no config key — stale LAPLACE_UCI env cannot override install root.
        var probe = ChessLabPaths.ResolveLaplaceUciForTest(uci);
        Assert.True(probe.Found);
        Assert.Equal(uci, probe.Path);
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
    public void DeployedLaplaceUciPath_IsInstallRootNeighbor()
    {
        var name = OperatingSystem.IsWindows() ? "laplace-uci.exe" : "laplace-uci";
        Assert.Equal(
            Path.Combine(LaplaceInstall.InstallRoot, name),
            ChessLabPaths.DeployedLaplaceUciPath);
    }

    [Fact]
    public void LabDir_UsesTempRoot()
    {
        Assert.StartsWith(Path.GetTempPath(), ChessLabPaths.LabDir, StringComparison.OrdinalIgnoreCase);
    }
}
