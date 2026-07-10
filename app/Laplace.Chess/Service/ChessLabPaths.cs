using System.Diagnostics;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

public static class ChessLabPaths
{
    private const string ChessLabEnvFile = "chess-lab.env";

    private static readonly string[] CutechessPathNames = ["cutechess-cli.exe", "cutechess-cli"];
    private static readonly string[] StockfishPathNames = ["stockfish.exe", "stockfish"];
    private static readonly string[] LaplaceUciPathNames = ["laplace-uci.exe", "laplace-uci"];

    public readonly record struct Probe(string? Path, bool Found, string Source);

    /// <summary>Deployed API hosts ship <c>laplace-uci</c> (<c>.exe</c> on Windows) beside the entry assembly.</summary>
    public static string DeployedLaplaceUciPath => Path.Combine(
        LaplaceInstall.InstallRoot,
        OperatingSystem.IsWindows() ? "laplace-uci.exe" : "laplace-uci");

    public static string LabDir
    {
        get
        {
            var fromConfig = LaplaceInstall.TryReadConfig("LAPLACE_CHESS_LAB_DIR", ChessLabEnvFile);
            return !string.IsNullOrWhiteSpace(fromConfig)
                ? fromConfig.Trim()
                : Path.Combine(Path.GetTempPath(), "laplace-chess-lab");
        }
    }

    public static Probe Cutechess => ResolveExecutable(
        "LAPLACE_CUTECHESS",
        _ => TryDefaultCutechessCandidate(),
        CutechessPathNames);

    public static Probe Stockfish => ResolveExecutable(
        "LAPLACE_STOCKFISH",
        _ => TryDefaultCutechessCandidate(OperatingSystem.IsWindows() ? "stockfish.exe" : "stockfish"),
        StockfishPathNames);

    public static Probe LaplaceUci => ResolveLaplaceUci();

    public static Probe QtBin => ResolveQtBin();

    public static IReadOnlyDictionary<string, Probe> Catalog => new Dictionary<string, Probe>(StringComparer.OrdinalIgnoreCase)
    {
        ["cutechess"] = Cutechess,
        ["stockfish"] = Stockfish,
        ["qt"] = QtBin,
        ["laplaceUci"] = LaplaceUci,
    };

    public static bool AllReady()
    {
        var c = Catalog;
        return c["cutechess"].Found && c["stockfish"].Found && c["qt"].Found && c["laplaceUci"].Found;
    }

    internal static Probe ResolveExecutableForTest(
        string? configValue,
        Func<string, string?>? repoCandidate,
        string[] pathNames,
        string? assemblyNeighbor = null)
        => ResolveExecutableCore(configValue, repoCandidate, pathNames, assemblyNeighbor);

    internal static Probe ResolveLaplaceUciForTest(string? installExe, Func<string, string?>? buildOutput = null)
    {
        if (!string.IsNullOrEmpty(installExe) && File.Exists(installExe))
            return new Probe(installExe, true, "install");

        if (LaplaceInstall.TryRepoRoot(out var root) && buildOutput is not null)
        {
            var built = buildOutput(root);
            if (!string.IsNullOrEmpty(built) && File.Exists(built))
                return new Probe(built, true, "build");
        }

        return new Probe(installExe, false, "missing");
    }

    internal static Probe ResolveQtBinForTest(string? configValue)
    {
        if (!string.IsNullOrWhiteSpace(configValue))
        {
            var p = configValue.Trim();
            return new Probe(p, Directory.Exists(p), "config");
        }

        return new Probe(null, false, "missing");
    }

    private static Probe ResolveExecutable(
        string configKey,
        Func<string, string?>? repoCandidate,
        string[] pathNames,
        string? assemblyNeighbor = null)
        => ResolveExecutableCore(
            LaplaceInstall.TryReadConfig(configKey, ChessLabEnvFile),
            repoCandidate,
            pathNames,
            assemblyNeighbor);

    private static Probe ResolveLaplaceUci()
    {
        var installed = DeployedLaplaceUciPath;
        if (File.Exists(installed))
            return new Probe(installed, true, "install");

        if (TryResolveUciBuildOutput(out var built))
            return new Probe(built, true, "build");

        if (TryFindOnPath(LaplaceUciPathNames, out var pathHit))
            return new Probe(pathHit, true, "path");

        return new Probe(installed, false, "missing");
    }

    private static string? TryDefaultCutechessCandidate(string? name = null)
    {
        name ??= OperatingSystem.IsWindows() ? "cutechess-cli.exe" : "cutechess-cli";
        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_CUTECHESS_BUILD");
        if (string.IsNullOrWhiteSpace(fromEnv) && OperatingSystem.IsWindows())
            fromEnv = Path.Combine(LaplaceInstall.DefaultBuildRoot, "build-cutechess");
        if (string.IsNullOrWhiteSpace(fromEnv) && !OperatingSystem.IsWindows())
            fromEnv = "/opt/laplace/build-cutechess";
        if (string.IsNullOrWhiteSpace(fromEnv))
            return null;
        return Path.Combine(fromEnv.Trim(), name);
    }

    private static bool TryResolveUciBuildOutput(out string path)
    {
        path = "";
        if (!LaplaceInstall.TryDefaultBuildRoot(out var buildRoot))
            return false;

        var names = OperatingSystem.IsWindows()
            ? new[] { "laplace-uci.exe" }
            : new[] { "laplace-uci", "laplace-uci.exe" };
        foreach (var cfg in new[] { "Release", "Debug" })
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(
                    buildRoot.Trim(), "app", "bin", "Laplace.Chess.Uci", cfg, "net10.0", name);
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static Probe ResolveExecutableCore(
        string? configPath,
        Func<string, string?>? repoCandidate,
        string[] pathNames,
        string? assemblyNeighbor)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var p = configPath.Trim();
            if (File.Exists(p))
                return new Probe(p, true, "config");
        }

        LaplaceInstall.TryDefaultBuildRoot(out var buildRoot);
        if (repoCandidate is not null)
        {
            var repoPath = repoCandidate(buildRoot);
            if (!string.IsNullOrEmpty(repoPath) && File.Exists(repoPath))
                return new Probe(repoPath, true, "build");
        }

        if (!string.IsNullOrEmpty(assemblyNeighbor) && File.Exists(assemblyNeighbor))
            return new Probe(assemblyNeighbor, true, "path");

        if (TryFindOnPath(pathNames, out var pathHit))
            return new Probe(pathHit, true, "path");

        var missing = !string.IsNullOrWhiteSpace(configPath) ? configPath.Trim()
            : repoCandidate is not null ? repoCandidate(buildRoot)
            : assemblyNeighbor;
        return new Probe(missing, false, "missing");
    }

    private static Probe ResolveQtBin()
    {
        var fromConfig = LaplaceInstall.TryReadConfig("LAPLACE_QT_BIN", ChessLabEnvFile);
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            var p = fromConfig.Trim();
            if (Directory.Exists(p))
                return new Probe(p, true, "config");
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var p in new[]
                     {
                         "/usr/lib/qt6/bin",
                         "/usr/lib/x86_64-linux-gnu/qt6/bin",
                         "/usr/lib/x86_64-linux-gnu",
                     })
            {
                if (Directory.Exists(p))
                    return new Probe(p, true, "system");
            }
        }

        return new Probe(null, false, "missing");
    }

    private static bool TryFindOnPath(string[] names, out string found)
    {
        found = "";
        foreach (var name in names)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "where.exe",
                        Arguments = name,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    if (proc is null) continue;
                    var line = proc.StandardOutput.ReadLine()?.Trim();
                    proc.WaitForExit();
                    if (!string.IsNullOrEmpty(line) && File.Exists(line))
                    {
                        found = line;
                        return true;
                    }
                }
                else
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "/usr/bin/which",
                        Arguments = name,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    if (proc is null) continue;
                    var line = proc.StandardOutput.ReadLine()?.Trim();
                    proc.WaitForExit();
                    if (!string.IsNullOrEmpty(line) && File.Exists(line))
                    {
                        found = line;
                        return true;
                    }

                    // Debian/Ubuntu stockfish lives in /usr/games (often not on PATH for services).
                    if (name == "stockfish" && File.Exists("/usr/games/stockfish"))
                    {
                        found = "/usr/games/stockfish";
                        return true;
                    }
                }
            }
            catch
            {
                // ignore — fall through to missing probe
            }
        }

        return false;
    }
}
