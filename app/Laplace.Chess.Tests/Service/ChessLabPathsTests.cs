using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessLabPathsTests
{
    [Fact]
    public void SourceStockfishPrecedesInstalledBuildAndPathButPreservesExplicitBinary()
    {
        string root = Path.Combine(Path.GetTempPath(), $"stockfish-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            var installed = Path.Combine(root, "installed");
            var explicitPath = Path.Combine(root, "operator-selected");
            foreach (var path in new[] { source, installed, explicitPath }) File.WriteAllText(path, "engine");
            Assert.Equal(new ChessLabPaths.Probe(source, true, "source"),
                ChessLabPaths.ResolveExecutableForTest(null, _ => installed, ["stockfish"],
                    installedCandidate: installed, sourceCandidate: source));
            Assert.Equal(new ChessLabPaths.Probe(explicitPath, true, "config"),
                ChessLabPaths.ResolveExecutableForTest(explicitPath, _ => installed, ["stockfish"],
                    installedCandidate: installed, sourceCandidate: source));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SyzygyDefaultIncludesSmallerAndLargerSetsAndPreservesExplicitSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"syzygy-paths-{Guid.NewGuid():N}");
        var packageRoot = Path.Combine(root, "Games", "Chess", "syzygy");
        var smaller = Path.Combine(packageRoot, "3-4-5");
        var larger = Path.Combine(packageRoot, "6");
        Directory.CreateDirectory(smaller);
        Directory.CreateDirectory(larger);
        try
        {
            Assert.False(ChessLabPaths.ResolveSyzygyDirCore(null, root).Found);
            File.WriteAllBytes(Path.Combine(smaller, "KQvK.rtbw"), [0]);
            File.WriteAllBytes(Path.Combine(larger, "KPPvKPP.rtbw"), [0]);
            Assert.Equal(new ChessLabPaths.Probe(packageRoot, true, "data-root"),
                ChessLabPaths.ResolveSyzygyDirCore(null, root));
            Assert.Equal(new ChessLabPaths.Probe(smaller, true, "config"),
                ChessLabPaths.ResolveSyzygyDirCore(smaller, root));
            var missing = Path.Combine(root, "missing");
            Assert.Equal(new ChessLabPaths.Probe(missing, false, "config"),
                ChessLabPaths.ResolveSyzygyDirCore(missing, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ManagedStockfishPrecedesBuildAndPathButPreservesExplicitOverrides()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"stockfish-paths-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var installed = Path.Combine(dir, "installed");
            var custom = Path.Combine(dir, "custom");
            File.WriteAllText(installed, "managed");
            File.WriteAllText(custom, "operator");
            var probe = ChessLabPaths.ResolveExecutableForTest(null, _ => custom, ["stockfish"],
                installedCandidate: installed);
            Assert.Equal(new ChessLabPaths.Probe(installed, true, "install"), probe);
            probe = ChessLabPaths.ResolveExecutableForTest(custom, _ => installed, ["stockfish"],
                installedCandidate: installed);
            Assert.Equal(new ChessLabPaths.Probe(custom, true, "config"), probe);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

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
    public void QtBin_UsesConfigPathContainingRuntimeLibrary()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qt-bin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            string name = OperatingSystem.IsWindows() ? "Qt6Core.dll"
                : OperatingSystem.IsMacOS() ? "libQt6Core.6.dylib" : "libQt6Core.so.6";
            File.WriteAllText(Path.Combine(dir, name), "runtime fixture");
            var probe = ChessLabPaths.ResolveQtBinForTest(dir);
            Assert.Equal("config", probe.Source);
            Assert.Equal(dir, probe.Path);
            Assert.True(probe.Found);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void QtBin_ArbitraryExistingDirectoryDoesNotProveQtIsInstalled()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qt-absent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "unrelated-library.so"), "unrelated");
            var probe = ChessLabPaths.ResolveQtBinForTest(dir);
            Assert.Equal("config", probe.Source);
            Assert.Equal(dir, probe.Path);
            Assert.False(probe.Found);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void QtBin_RecognizesExecutableSdkQmake()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qt-sdk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var qmake = Path.Combine(dir, OperatingSystem.IsWindows() ? "qmake.exe" : "qmake");
            File.WriteAllText(qmake, "sdk fixture");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(qmake, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Assert.False(ChessLabPaths.ResolveQtBinForTest(dir).Found);
                File.SetUnixFileMode(qmake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Assert.True(ChessLabPaths.ResolveQtBinForTest(dir).Found);
        }
        finally { Directory.Delete(dir, recursive: true); }
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
