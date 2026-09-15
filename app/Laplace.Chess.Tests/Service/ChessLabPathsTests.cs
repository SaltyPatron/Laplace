using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessLabPathsTests
{
    [Fact]
    public void InstalledChessSelectionAndEvaluationSettingsAreSharedByCliAndService()
    {
        string root = Path.Combine(Path.GetTempPath(), $"chess-config-{Guid.NewGuid():N}");
        string source = Path.Combine(root, "operator-source");
        string binary = Path.Combine(source, "src", OperatingSystem.IsWindows() ? "stockfish.exe" : "stockfish");
        string explicitBinary = Path.Combine(root, "explicit-engine");
        string[] keys = ["LAPLACE_INSTALL_PREFIX", "LAPLACE_STOCKFISH", "LAPLACE_STOCKFISH_SOURCE",
            "LAPLACE_EXTERNAL", "LAPLACE_STOCKFISH_EVAL_THREADS", "LAPLACE_STOCKFISH_EVAL_HASH_MB",
            "LAPLACE_STOCKFISH_EVAL_NUMA_POLICY", "LAPLACE_STOCKFISH_EVAL_PROCESSES",
            "LAPLACE_STOCKFISH_EVAL_SYZYGY_PATH", "LAPLACE_STOCKFISH_EVAL_FILE",
            "LAPLACE_STOCKFISH_EVAL_TIMEOUT_SECONDS"];
        var previous = keys.ToDictionary(static key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable("LAPLACE_INSTALL_PREFIX", root);
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            Directory.CreateDirectory(Path.Combine(root, "app"));
            Directory.CreateDirectory(Path.Combine(root, "secrets"));
            File.WriteAllText(binary, "source engine");
            File.WriteAllText(explicitBinary, "explicit engine");
            File.WriteAllText(Path.Combine(root, "app", "laplace-api.env"),
                $"LAPLACE_STOCKFISH_SOURCE=/stale\nLAPLACE_STOCKFISH_SOURCE=\"{source}\"\n"
                + "LAPLACE_STOCKFISH_EVAL_THREADS=4\nLAPLACE_STOCKFISH_EVAL_NUMA_POLICY=none\n");
            File.WriteAllText(Path.Combine(root, "secrets", "chess-lab.env"),
                "LAPLACE_STOCKFISH_SOURCE=/stale-secret\nLAPLACE_STOCKFISH_EVAL_THREADS=8\n"
                + "LAPLACE_STOCKFISH_EVAL_HASH_MB=64\n");

            // A direct CLI process has no service EnvironmentFile loaded.
            var cliPath = ChessLabPaths.Stockfish;
            var cliOptions = StockfishEvaluationOptions.FromEnvironment(12, 1000);
            Assert.Equal(new ChessLabPaths.Probe(binary, true, "source"), cliPath);
            Assert.Equal(4, cliOptions.Threads);
            Assert.Equal(64, cliOptions.HashMb);
            Assert.Equal("none", cliOptions.NumaPolicy);

            // The API service receives those same installed assignments in its environment.
            Environment.SetEnvironmentVariable("LAPLACE_STOCKFISH_SOURCE", source);
            Environment.SetEnvironmentVariable("LAPLACE_STOCKFISH_EVAL_THREADS", "4");
            Environment.SetEnvironmentVariable("LAPLACE_STOCKFISH_EVAL_NUMA_POLICY", "none");
            Assert.Equal(cliPath, ChessLabPaths.Stockfish);
            Assert.Equal(cliOptions, StockfishEvaluationOptions.FromEnvironment(12, 1000));

            Environment.SetEnvironmentVariable("LAPLACE_STOCKFISH", explicitBinary);
            Environment.SetEnvironmentVariable("LAPLACE_STOCKFISH_EVAL_THREADS", "2");
            Assert.Equal(new ChessLabPaths.Probe(explicitBinary, true, "config"), ChessLabPaths.Stockfish);
            Assert.Equal(2, StockfishEvaluationOptions.FromEnvironment(12, 1000).Threads);
            Environment.SetEnvironmentVariable("LAPLACE_STOCKFISH", null);
            Environment.SetEnvironmentVariable("LAPLACE_STOCKFISH_SOURCE", null);
            File.AppendAllText(Path.Combine(root, "app", "laplace-api.env"),
                $"LAPLACE_STOCKFISH={explicitBinary}\n");
            Assert.Equal(new ChessLabPaths.Probe(explicitBinary, true, "config"), ChessLabPaths.Stockfish);
            Environment.SetEnvironmentVariable("LAPLACE_STOCKFISH_SOURCE", source);
            Assert.Equal(new ChessLabPaths.Probe(binary, true, "source"), ChessLabPaths.Stockfish);
            File.Delete(binary);
            Assert.Equal(new ChessLabPaths.Probe(binary, false, "source"), ChessLabPaths.Stockfish);
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
            Directory.Delete(root, recursive: true);
        }
    }

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
            File.Delete(explicitPath);
            Assert.Equal(new ChessLabPaths.Probe(explicitPath, false, "config"),
                ChessLabPaths.ResolveExecutableForTest(explicitPath, _ => installed, ["stockfish"],
                    installedCandidate: installed, sourceCandidate: source));
            File.Delete(source);
            Assert.Equal(new ChessLabPaths.Probe(source, false, "source"),
                ChessLabPaths.ResolveExecutableForTest(null, _ => installed, ["stockfish"],
                    installedCandidate: installed, sourceCandidate: source, sourceAuthoritative: true));
            Assert.Equal(new ChessLabPaths.Probe(installed, true, "install"),
                ChessLabPaths.ResolveExecutableForTest(null, _ => installed, ["stockfish"],
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
            Assert.False(ChessLabPaths.ResolveSyzygyDirCore(null, root).Found);
            File.WriteAllBytes(Path.Combine(smaller, "KQvK.rtbz"), [0]);
            File.WriteAllBytes(Path.Combine(larger, "KPPvKPP.rtbz"), [0]);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void LabDir_WithoutSelectionUsesTempRoot(string? selection)
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), "laplace-chess-lab"),
            ChessLabPaths.ResolveLabDirCore(selection));
    }

    [Fact]
    public void LabDir_PreservesConfiguredWorkingDirectory()
    {
        string configured = Path.Combine(Path.GetTempPath(), "selected-chess-work");
        Assert.Equal(configured, ChessLabPaths.ResolveLabDirCore($"  {configured}  "));
    }
}
