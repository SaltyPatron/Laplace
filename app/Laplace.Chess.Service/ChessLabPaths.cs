namespace Laplace.Chess.Service;

/// <summary>Resolves Chess Lab external binaries from env vars, repo-relative paths, or PATH.</summary>
public static class ChessLabPaths
{
    public const string EnvCutechess = "LAPLACE_CUTECHESS";
    public const string EnvStockfish = "LAPLACE_STOCKFISH";
    public const string EnvQtBin = "LAPLACE_QT_BIN";
    public const string EnvUci = "LAPLACE_UCI";
    public const string EnvLabDir = "LAPLACE_CHESS_LAB_DIR";

    private static readonly string[] CutechessPathNames = ["cutechess-cli.exe", "cutechess-cli"];
    private static readonly string[] StockfishPathNames = ["stockfish.exe", "stockfish"];
    private static readonly string[] LaplaceUciPathNames = ["laplace-uci.exe", "laplace-uci"];

    public readonly record struct Probe(string? Path, bool Found, string Source);

    /// <summary>Load <c>deploy/secrets/chess-lab.env</c> into the process environment (does not override existing vars).</summary>
    public static void LoadEnvFile(string? path = null)
    {
        foreach (var candidate in EnvFileCandidates(path))
        {
            if (!File.Exists(candidate)) continue;
            foreach (var line in File.ReadLines(candidate))
            {
                if (line.TrimStart().StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();
                if (string.IsNullOrEmpty(key)) continue;
                if (Environment.GetEnvironmentVariable(key) is null)
                    Environment.SetEnvironmentVariable(key, val);
            }
            return;
        }
    }

    public static string LabDir =>
        Environment.GetEnvironmentVariable(EnvLabDir)
        ?? Path.Combine(Path.GetTempPath(), "laplace-chess-lab");

    public static Probe Cutechess => ResolveExecutable(
        EnvCutechess,
        repoRoot => Path.Combine(repoRoot, "build-cutechess", "cutechess-cli.exe"),
        CutechessPathNames);

    public static Probe Stockfish => ResolveExecutable(
        EnvStockfish,
        _ => null,
        StockfishPathNames);

    public static Probe LaplaceUci => ResolveExecutable(
        EnvUci,
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

    public static Probe QtBin => ResolveDirectory(EnvQtBin);

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

    private static Probe ResolveExecutable(
        string envName,
        Func<string, string?>? repoCandidate,
        string[] pathNames,
        string? assemblyNeighbor = null)
        => ResolveExecutableCore(Environment.GetEnvironmentVariable(envName), repoCandidate, pathNames, assemblyNeighbor);

    private static Probe ResolveExecutableCore(
        string? envValue,
        Func<string, string?>? repoCandidate,
        string[] pathNames,
        string? assemblyNeighbor)
    {
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            var p = envValue.Trim();
            return new Probe(p, File.Exists(p), "env");
        }

        if (TryRepoRoot(out var root) && repoCandidate is not null)
        {
            var repoPath = repoCandidate(root);
            if (!string.IsNullOrEmpty(repoPath))
                return new Probe(repoPath, File.Exists(repoPath), "repo");
        }

        if (!string.IsNullOrEmpty(assemblyNeighbor) && File.Exists(assemblyNeighbor))
            return new Probe(assemblyNeighbor, true, "path");

        if (TryFindOnPath(pathNames, out var pathHit))
            return new Probe(pathHit, true, "path");

        var missing = repoCandidate is not null && TryRepoRoot(out root)
            ? repoCandidate(root)
            : assemblyNeighbor;
        return new Probe(missing, false, "missing");
    }

    private static Probe ResolveDirectory(string envName)
    {
        var envValue = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            var p = envValue.Trim();
            return new Probe(p, Directory.Exists(p), "env");
        }

        return new Probe(null, false, "missing");
    }

    private static IEnumerable<string> EnvFileCandidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath;

        var fromBase = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "deploy", "secrets", "chess-lab.env"));
        yield return fromBase;

        if (TryRepoRoot(out var root))
            yield return Path.Combine(root, "deploy", "secrets", "chess-lab.env");
    }

    private static bool TryRepoRoot(out string root)
    {
        var env = Environment.GetEnvironmentVariable("LAPLACE_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            root = Path.GetFullPath(env);
            return true;
        }

        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "app")) && Directory.Exists(Path.Combine(dir, "engine")))
            {
                root = dir;
                return true;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        root = "";
        return false;
    }

    private static bool TryFindOnPath(string[] names, out string found)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            found = "";
            return false;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    found = candidate;
                    return true;
                }
            }
        }

        found = "";
        return false;
    }
}
