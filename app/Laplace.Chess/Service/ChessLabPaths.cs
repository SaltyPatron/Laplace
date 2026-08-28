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
        StockfishPathNames,
        installedCandidate: OperatingSystem.IsWindows() ? null : Path.Combine(
            Environment.GetEnvironmentVariable("LAPLACE_INSTALL_PREFIX") is { Length: > 0 } prefix
                ? prefix : "/opt/laplace", "bin", "stockfish"));

    public static Probe LaplaceUci => ResolveLaplaceUci();

    public static Probe QtBin => ResolveQtBin();

    /// <summary>
    /// Syzygy tablebase DIRECTORY (WDL <c>.rtbw</c> + DTZ <c>.rtbz</c> files) for the
    /// ChessSyzygy probe lane. Env <c>LAPLACE_SYZYGY</c> or <c>chess-lab.env</c> first;
    /// then the corpus layout under <c>LAPLACE_DATA_ROOT</c>.
    ///
    /// The env lookup used to be the only lookup, and the tables were downloaded to
    /// <c>/vault/Data/Games/Chess/syzygy/3-4-5</c> (290 files, ~1 GB, present on this
    /// host since 2026-07-30). Nobody exported the variable, so <c>ingest chess-syzygy</c>
    /// no-op'd on a box that had the tablebases sitting on disk the whole time. A
    /// downloaded dependency the code cannot find is the same as one that was never
    /// downloaded, and only one of those is the operator's problem to fix.
    ///
    /// Still a clean no-op when genuinely absent — the tables are an opt-in download.
    /// </summary>
    public static Probe SyzygyDir => ResolveSyzygyDir();

    /// <summary>Corpus-layout locations, most specific first, under the ingest data root.</summary>
    private static IEnumerable<string> DefaultSyzygyCandidates()
    {
        string root = Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT") is { Length: > 0 } r
            ? r
            : OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data";
        string baseDir = Path.Combine(root, "Games", "Chess", "syzygy");
        // A tablebase set is stored one directory per men-count bracket ("3-4-5", "6").
        // Deepest set first so a box holding 6-man tables uses them.
        yield return Path.Combine(baseDir, "6");
        yield return Path.Combine(baseDir, "3-4-5");
        yield return baseDir;
    }

    private static Probe ResolveSyzygyDir()
    {
        var configured = ResolveDirectory("LAPLACE_SYZYGY");
        if (configured.Found) return configured;

        foreach (var candidate in DefaultSyzygyCandidates())
        {
            if (!Directory.Exists(candidate)) continue;
            // A directory holding no tables is not a tablebase directory — probing it
            // would report "0 tables discovered" as though the operator misconfigured it.
            if (!Directory.EnumerateFiles(candidate, "*.rtbw").Any()) continue;
            return new Probe(candidate, true, "data-root");
        }
        return configured;
    }

    public static IReadOnlyDictionary<string, Probe> Catalog => new Dictionary<string, Probe>(StringComparer.OrdinalIgnoreCase)
    {
        ["cutechess"] = Cutechess,
        ["stockfish"] = Stockfish,
        ["qt"] = QtBin,
        ["laplaceUci"] = LaplaceUci,
        ["syzygy"] = SyzygyDir,
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
        string? assemblyNeighbor = null,
        string? installedCandidate = null)
        => ResolveExecutableCore(configValue, repoCandidate, pathNames, assemblyNeighbor, installedCandidate);

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
        string? assemblyNeighbor = null,
        string? installedCandidate = null)
        => ResolveExecutableCore(
            LaplaceInstall.TryReadConfig(configKey, ChessLabEnvFile),
            repoCandidate,
            pathNames,
            assemblyNeighbor,
            installedCandidate);

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
        string? assemblyNeighbor,
        string? installedCandidate)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var p = configPath.Trim();
            if (File.Exists(p))
                return new Probe(p, true, "config");
        }

        // CLI/ingest callers do not inherit the API's environment file. Prefer
        // the managed release before build/PATH (which can select distro 14.1).
        if (!string.IsNullOrEmpty(installedCandidate) && File.Exists(installedCandidate))
            return new Probe(installedCandidate, true, "install");

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

    private static Probe ResolveDirectory(string configKey)
    {
        var fromConfig = LaplaceInstall.TryReadConfig(configKey, ChessLabEnvFile);
        if (string.IsNullOrWhiteSpace(fromConfig))
            return new Probe(null, false, "missing");
        var p = fromConfig.Trim();
        return new Probe(p, Directory.Exists(p), "config");
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
