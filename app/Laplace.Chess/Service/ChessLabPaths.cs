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

    public static string LabDir
    {
        get
        {
            var fromSecret = LaplaceInstall.TryReadDeploySecret(ChessLabEnvFile, "LAPLACE_CHESS_LAB_DIR");
            return !string.IsNullOrWhiteSpace(fromSecret)
                ? fromSecret.Trim()
                : Path.Combine(Path.GetTempPath(), "laplace-chess-lab");
        }
    }

    public static Probe Cutechess => ResolveExecutable(
        "LAPLACE_CUTECHESS",
        repoRoot => Path.Combine(repoRoot, "build-cutechess", "cutechess-cli.exe"),
        CutechessPathNames);

    public static Probe Stockfish => ResolveExecutable(
        "LAPLACE_STOCKFISH",
        repoRoot => Path.Combine(repoRoot, "build-cutechess", "stockfish.exe"),
        StockfishPathNames);

    public static Probe LaplaceUci => ResolveExecutable(
        "LAPLACE_UCI",
        repoRoot =>
        {
            foreach (var rel in new[]
            {
                Path.Combine("app", "Laplace.Chess.Uci", "bin", "Release", "net10.0", "laplace-uci.exe"),
                Path.Combine("app", "Laplace.Cli", "bin", "Release", "net10.0", "laplace-uci.exe"),
            })
            {
                var p = Path.Combine(repoRoot, rel);
                if (File.Exists(p)) return p;
            }
            return null;
        },
        LaplaceUciPathNames,
        Path.Combine(AppContext.BaseDirectory, "laplace-uci.exe"));

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
        string? envValue,
        Func<string, string?>? repoCandidate,
        string[] pathNames,
        string? assemblyNeighbor = null)
        => ResolveExecutableCore(envValue, repoCandidate, pathNames, assemblyNeighbor);

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
            LaplaceInstall.TryReadDeploySecret(ChessLabEnvFile, configKey),
            repoCandidate,
            pathNames,
            assemblyNeighbor);

    private static Probe ResolveExecutableCore(
        string? explicitPath,
        Func<string, string?>? repoCandidate,
        string[] pathNames,
        string? assemblyNeighbor)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var p = explicitPath.Trim();
            if (File.Exists(p))
                return new Probe(p, true, "config");
        }

        if (LaplaceInstall.TryRepoRoot(out var root) && repoCandidate is not null)
        {
            var repoPath = repoCandidate(root);
            if (!string.IsNullOrEmpty(repoPath))
                return new Probe(repoPath, File.Exists(repoPath), "repo");
        }

        if (!string.IsNullOrEmpty(assemblyNeighbor) && File.Exists(assemblyNeighbor))
            return new Probe(assemblyNeighbor, true, "path");

        if (TryFindOnPath(pathNames, out var pathHit))
            return new Probe(pathHit, true, "path");

        var missing = repoCandidate is not null && LaplaceInstall.TryRepoRoot(out root)
            ? repoCandidate(root)
            : assemblyNeighbor;
        return new Probe(missing, false, "missing");
    }

    private static Probe ResolveQtBin()
    {
        var fromSecret = LaplaceInstall.TryReadDeploySecret(ChessLabEnvFile, "LAPLACE_QT_BIN");
        if (!string.IsNullOrWhiteSpace(fromSecret))
        {
            var p = fromSecret.Trim();
            if (Directory.Exists(p))
                return new Probe(p, true, "config");
        }

        return new Probe(null, false, "missing");
    }

    private static bool TryFindOnPath(string[] names, out string found)
    {
        found = "";
        if (!OperatingSystem.IsWindows()) return false;

        foreach (var name in names)
        {
            try
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
            catch
            {
                // ignore — fall through to missing probe
            }
        }

        return false;
    }
}
