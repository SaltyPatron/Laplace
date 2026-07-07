using System.Reflection;

namespace Laplace.Engine.Core;

/// <summary>
/// Install-time discovery: repo layout, Postgres, perfcache, ingest data.
/// No environment variables, no config files — walk the filesystem from the DLL.
/// </summary>
public static class LaplaceInstall
{
    public const int EndpointPort = 5187;

    public static string InstallRoot => AppContext.BaseDirectory;

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
        var s = OperatingSystem.IsWindows()
            ? $"Host=localhost;Username=postgres;Password=postgres;Database={database};Command Timeout=0"
            : $"Host=/var/run/postgresql;Username=laplace_admin;Database={database}-dev";

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
        if (TryRepoRoot(out var root))
        {
            var fromBuild = Path.Combine(root, "build-win", "core", "perfcache", "laplace_t0_perfcache.bin");
            if (File.Exists(fromBuild)) return fromBuild;
            foreach (var build in Directory.EnumerateDirectories(root, "build*"))
            {
                var hit = Directory.EnumerateFiles(build, "laplace_t0_perfcache.bin", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
        }

        const string share = "/opt/laplace/share/laplace";
        if (Directory.Exists(share))
        {
            var hit = Directory.EnumerateFiles(share, "laplace_t0_perfcache*.bin").FirstOrDefault();
            if (hit is not null) return hit;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var build in dir.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName, "laplace_t0_perfcache.bin",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
        }

        throw new InvalidOperationException(
            "T0 perfcache not found — build the engine (build-win/core/perfcache/laplace_t0_perfcache.bin).");
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

        if (TryRepoRoot(out var root))
        {
            var fromBuild = Path.Combine(root, "build-win", "core", "perfcache", "laplace_highway_perfcache.bin");
            if (File.Exists(fromBuild)) return fromBuild;
            foreach (var build in Directory.EnumerateDirectories(root, "build*"))
            {
                var hit = Directory.EnumerateFiles(build, "laplace_highway_perfcache.bin", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (hit is not null) return hit;
            }
        }

        for (var walk = new DirectoryInfo(AppContext.BaseDirectory); walk is not null; walk = walk.Parent)
        {
            foreach (var build in walk.EnumerateDirectories("build*"))
            {
                var hit = Directory.EnumerateFiles(build.FullName, "laplace_highway_perfcache.bin",
                    SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
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
        if (TryRepoRoot(out var root))
            return Path.GetFullPath(Path.Combine(root, "out", "models"));

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
}
