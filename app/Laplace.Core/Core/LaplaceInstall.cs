using System.Reflection;

namespace Laplace.Engine.Core;

/// <summary>
/// Install-time discovery: repo layout, Postgres, perfcache, ingest data.
/// Postgres uses LAPLACE_DB when set (IIS/deploy); otherwise localhost defaults.
/// </summary>
public static class LaplaceInstall
{
    public const int EndpointPort = 5187;

    public static string InstallRoot => AppContext.BaseDirectory;

    /// <summary>Windows default matches scripts/win/env.cmd when LAPLACE_BUILD_ROOT is unset.</summary>
    public static string DefaultBuildRoot =>
        OperatingSystem.IsWindows() ? @"D:\Data\Laplace" : "/vault/Data";

    public static bool TryDefaultBuildRoot(out string buildRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_BUILD_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            buildRoot = Path.GetFullPath(fromEnv.Trim());
            return true;
        }

        buildRoot = DefaultBuildRoot;
        return true;
    }

    public static string WebRoot => Path.Combine(InstallRoot, "wwwroot");

    public static string EndpointBaseUrl => $"http://127.0.0.1:{EndpointPort}";

    public static bool TryRepoRoot(out string root)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "app")) && Directory.Exists(Path.Combine(dir, "engine")))
            {
                root = Path.GetFullPath(dir);
                return true;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        var stamped = TryStampedRepoRoot();
        if (!string.IsNullOrEmpty(stamped)
            && Directory.Exists(Path.Combine(stamped, "app"))
            && Directory.Exists(Path.Combine(stamped, "engine")))
        {
            root = Path.GetFullPath(stamped);
            return true;
        }

        root = "";
        return false;
    }

    public static string PostgresConnectionString(string database = "laplace")
    {
        var dbName = OperatingSystem.IsWindows()
            ? database
            : ResolveLinuxDatabaseName(database);

        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_DB");
        var s = !string.IsNullOrWhiteSpace(fromEnv)
            ? WithDatabase(fromEnv.Trim(), dbName)
            : OperatingSystem.IsWindows()
                ? $"Host=localhost;Username=postgres;Password=postgres;Database={dbName};Command Timeout=0"
                : $"Host=/var/run/postgresql;Username=laplace_admin;Database={dbName}";

        return EnsurePostgresConnectionDefaults(s);
    }

    private static string WithDatabase(string connectionString, string database)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var replaced = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(parts[i][..eq].Trim(), "Database", StringComparison.OrdinalIgnoreCase)) continue;
            parts[i] = $"Database={database}";
            replaced = true;
            break;
        }

        if (!replaced)
            parts = [..parts, $"Database={database}"];

        return string.Join(';', parts);
    }

    private static string EnsurePostgresConnectionDefaults(string s)
    {
        if (!s.Contains("Include Error Detail", StringComparison.OrdinalIgnoreCase))
            s += ";Include Error Detail=true";
        if (!s.Contains("Search Path", StringComparison.OrdinalIgnoreCase))
            s += ";Search Path=laplace,public";
        if (!s.Contains("Command Timeout", StringComparison.OrdinalIgnoreCase)
            && !s.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            s += ";Command Timeout=0";
        return s;
    }

    public static string ResolveT0Perfcache()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv.Trim()))
            return Path.GetFullPath(fromEnv.Trim());

        if (TryResolveEngineBuildRoot(out var engineBuild))
        {
            var fromBuild = Path.Combine(engineBuild, "core", "perfcache", "laplace_t0_perfcache.bin");
            if (File.Exists(fromBuild)) return fromBuild;
            foreach (var hit in Directory.EnumerateFiles(engineBuild, "laplace_t0_perfcache.bin", SearchOption.AllDirectories))
                return hit;
        }

        const string share = "/opt/laplace/share/laplace";
        if (Directory.Exists(share))
        {
            var hit = Directory.EnumerateFiles(share, "laplace_t0_perfcache*.bin").FirstOrDefault();
            if (hit is not null) return hit;
        }

        throw new InvalidOperationException(
            "T0 perfcache not found — build the engine (D:\\Data\\Laplace\\build-win\\core\\perfcache\\laplace_t0_perfcache.bin).");
    }

    public static string ResolveIngestRoot()
    {
        if (TryRepoRoot(out var root))
        {
            var drive = Path.GetPathRoot(root);
            if (!string.IsNullOrEmpty(drive))
            {
                var sibling = Path.Combine(drive, "Data", "Ingest");
                if (Directory.Exists(sibling)) return Path.GetFullPath(sibling);
            }

            var upTwo = Path.GetFullPath(Path.Combine(root, "..", "..", "Data", "Ingest"));
            if (Directory.Exists(upTwo)) return upTwo;
        }

        if (OperatingSystem.IsWindows())
        {
            const string win = @"D:\Data\Ingest";
            if (Directory.Exists(win)) return win;
        }

        if (Directory.Exists("/vault/Data")) return "/vault/Data";

        throw new InvalidOperationException(
            "Ingest data root not found — expected sibling Data/Ingest to the repo or /vault/Data.");
    }

    public static string ResolvePathUnderIngest(params string[] relative)
        => Path.GetFullPath(Path.Combine(ResolveIngestRoot(), Path.Combine(relative)));

    public static string ResolveDataRoot() => ResolveIngestRoot();

    public static string ResolveIso639Dir() => ResolvePathUnderIngest("ISO639");

    public static string ResolveCiliDir() => ResolvePathUnderIngest("CILI");

    public static string ResolveChessGamesDir() => ResolvePathUnderIngest("Games", "Chess");

    public static string ResolveHighwayPerfcache()
    {
        var t0 = ResolveT0Perfcache();
        var dir = Path.GetDirectoryName(t0);
        if (!string.IsNullOrEmpty(dir))
        {
            var sibling = Path.Combine(dir, "laplace_highway_perfcache.bin");
            if (File.Exists(sibling)) return sibling;
        }

        if (TryResolveEngineBuildRoot(out var engineBuild))
        {
            var fromBuild = Path.Combine(engineBuild, "core", "perfcache", "laplace_highway_perfcache.bin");
            if (File.Exists(fromBuild)) return fromBuild;
            foreach (var hit in Directory.EnumerateFiles(engineBuild, "laplace_highway_perfcache.bin", SearchOption.AllDirectories))
                return hit;
        }

        throw new InvalidOperationException(
            "Highway perfcache not found — build the engine (laplace_highway_perfcache.bin beside T0 blob).");
    }

    public static string ResolveModelHub()
    {
        if (TryRepoRoot(out var root))
        {
            var drive = Path.GetPathRoot(root);
            if (!string.IsNullOrEmpty(drive))
            {
                var hub = Path.Combine(drive, "Models", "hub");
                if (Directory.Exists(hub)) return Path.GetFullPath(hub);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            const string win = @"D:\Models\hub";
            if (Directory.Exists(win)) return win;
        }

        if (Directory.Exists("/vault/models/hub")) return "/vault/models/hub";

        throw new InvalidOperationException(
            "Model hub not found — expected D:\\Models\\hub or /vault/models/hub.");
    }

    public static string ResolveGgufOutputDir()
    {
        var outRoot = Environment.GetEnvironmentVariable("LAPLACE_OUT");
        if (!string.IsNullOrWhiteSpace(outRoot))
            return Path.GetFullPath(Path.Combine(outRoot.Trim(), "models"));

        var buildRoot = Environment.GetEnvironmentVariable("LAPLACE_BUILD_ROOT");
        if (!string.IsNullOrWhiteSpace(buildRoot))
            return Path.GetFullPath(Path.Combine(buildRoot.Trim(), "out", "models"));

        if (TryDefaultBuildRoot(out buildRoot))
            return Path.GetFullPath(Path.Combine(buildRoot, "out", "models"));

        var fallback = Path.Combine(AppContext.BaseDirectory, "models");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>Process environment first, then <c>deploy/secrets/{secretFile}</c>.</summary>
    public static string? TryReadConfig(string key, string? secretFile = null)
    {
        var fromEnv = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        return secretFile is null ? null : TryReadDeploySecret(secretFile, key);
    }

    /// <summary>Read a key from <c>deploy/secrets/{fileName}</c> when present.</summary>
    public static string? TryReadDeploySecret(string fileName, string key)
    {
        foreach (var path in DeploySecretCandidates(fileName))
        {
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path))
            {
                if (line.TrimStart().StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (!string.Equals(line[..eq].Trim(), key, StringComparison.Ordinal)) continue;
                var val = line[(eq + 1)..].Trim();
                return string.IsNullOrEmpty(val) ? null : val;
            }
        }

        return null;
    }

    private static string? TryStampedRepoRoot()
        => typeof(LaplaceInstall).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LaplaceRepoRoot")?.Value;

    private static bool TryResolveEngineBuildRoot(out string engineBuild)
    {
        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_ENGINE_BUILD");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            engineBuild = Path.GetFullPath(fromEnv.Trim());
            if (Directory.Exists(engineBuild)) return true;
        }

        fromEnv = Environment.GetEnvironmentVariable("LAPLACE_BUILD_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            engineBuild = Path.GetFullPath(Path.Combine(fromEnv.Trim(), "build-win"));
            if (Directory.Exists(engineBuild)) return true;
        }

        if (TryDefaultBuildRoot(out var buildRoot))
        {
            engineBuild = Path.GetFullPath(Path.Combine(buildRoot, "build-win"));
            if (Directory.Exists(engineBuild)) return true;
        }

        engineBuild = "";
        return false;
    }

    private static IEnumerable<string> DeploySecretCandidates(string fileName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in DeploySecretCandidatePaths(fileName))
        {
            if (seen.Add(candidate))
                yield return candidate;
        }
    }

    private static IEnumerable<string> DeploySecretCandidatePaths(string fileName)
    {
        yield return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "deploy", "secrets", fileName));

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "deploy", "secrets", fileName);

        if (TryRepoRoot(out var root))
            yield return Path.Combine(root, "deploy", "secrets", fileName);

        var stamped = TryStampedRepoRoot();
        if (!string.IsNullOrEmpty(stamped))
            yield return Path.Combine(stamped, "deploy", "secrets", fileName);
    }

    /// <summary>
    /// Linux two-DB law: PGDATABASE when set (CI/ingest targets laplace), else local
    /// sandbox laplace-dev; any other explicit name is used as-is.
    /// </summary>
    private static string ResolveLinuxDatabaseName(string database)
    {
        if (database != "laplace")
            return database;

        var fromEnv = Environment.GetEnvironmentVariable("PGDATABASE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        return "laplace-dev";
    }
}
