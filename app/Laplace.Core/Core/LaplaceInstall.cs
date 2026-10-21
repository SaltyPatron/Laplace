using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Laplace.Engine.Core;

/// <summary>
/// Install-time discovery: repo layout, Postgres, perfcache, ingest data.
/// Postgres uses LAPLACE_DB when set (IIS/deploy); otherwise localhost defaults.
/// </summary>
public static class LaplaceInstall
{
    public const int EndpointPort = 5187;

    public static string InstallRoot => AppContext.BaseDirectory;

    /// <summary>The active checkout's declared build root; installed runtimes do not require one.</summary>
    public static string DefaultBuildRoot =>
        TryDefaultBuildRoot(out var root) ? root : throw new InvalidOperationException("No build root declared. Use the development entry point or set LAPLACE_BUILD_ROOT.");

    public static bool TryDefaultBuildRoot(out string buildRoot)
    {
        var fromEnv = TryGetProcessOrMachineSetting("LAPLACE_BUILD_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            buildRoot = Path.GetFullPath(fromEnv.Trim());
            return true;
        }

        var declaredBuild = TryGetProcessOrMachineSetting("LAPLACE_BUILD");
        if (!string.IsNullOrWhiteSpace(declaredBuild) && TryRepoRoot(out var repoRoot))
        {
            var checkout = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (OperatingSystem.IsWindows())
                checkout = checkout.ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkout)))[..12].ToLowerInvariant();
            var workspaceId = $"{Path.GetFileName(checkout)}-{hash}";
            var candidate = Path.GetFullPath(Path.Combine(declaredBuild.Trim(), "worktrees", workspaceId));
            if (Directory.Exists(candidate))
            {
                buildRoot = candidate;
                return true;
            }
        }

        buildRoot = "";
        return false;
    }

    public static string WebRoot => Path.Combine(InstallRoot, "wwwroot");

    /// <summary>
    /// Where the console apps write their CSV diagnostic logs, which SQL reads back through
    /// ops.app_log / file_fdw. Services declare LAPLACE_OPS_LOG_DIR; interactive
    /// processes default to per-user state, including installed command-line tools.
    /// Created on first use by the file sink.
    /// </summary>
    public static string OpsLogDirectory
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_OPS_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return Path.GetFullPath(fromEnv.Trim());

            // Published releases are immutable too: even --help initializes logging.
            // Default writes must never create files beside installed executables.
            var state = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // GetFolderPath returns "" with no HOME (systemd units, containers); a relative
            // path there would resolve against the CWD, which may be the working tree.
            if (string.IsNullOrEmpty(state)) state = Path.GetTempPath();
            return Path.Combine(state, "laplace", "logs");
        }
    }

    public static string EndpointBaseUrl => $"http://127.0.0.1:{EndpointPort}";

    public static bool TryRepoRoot(out string root)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (IsSourceRoot(dir))
            {
                root = Path.GetFullPath(dir);
                return true;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        var stamped = TryStampedRepoRoot();
        if (!string.IsNullOrEmpty(stamped) && IsSourceRoot(stamped))
        {
            root = Path.GetFullPath(stamped);
            return true;
        }

        root = "";
        return false;
    }

    // Relocated build output also contains app/ and engine/. Only source markers
    // distinguish that output from the checkout whose path is stamped at build time.
    private static bool IsSourceRoot(string directory) =>
        File.Exists(Path.Combine(directory, "app", "Laplace.slnx"))
        && File.Exists(Path.Combine(directory, "engine", "CMakeLists.txt"));

    /// <summary>
    /// The application's own database: accounts, sessions, API keys, billing, the <c>app</c> schema.
    /// LAPLACE_APP_DB when it is set; otherwise the database LAPLACE_DB names, so a host with one
    /// database is unchanged. The content substrate never lives here.
    /// </summary>
    public static string AppConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_APP_DB");
        return string.IsNullOrWhiteSpace(fromEnv) ? PostgresConnectionString() : fromEnv.Trim();
    }

    /// <summary>True when LAPLACE_APP_DB names a database of its own for the <c>app</c> schema.</summary>
    public static bool HasSeparateAppDatabase =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAPLACE_APP_DB"));

    public static string PostgresConnectionString(string database = "laplace")
    {
        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_DB");
        string s;
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            // Precedence: an explicit non-default caller argument overrides the env;
            // otherwise an explicit Database= inside LAPLACE_DB is kept as given.
            if (database != "laplace")
                s = WithDatabase(fromEnv.Trim(),
                    OperatingSystem.IsWindows() ? database : ResolveLinuxDatabaseName(database));
            else if (HasDatabase(fromEnv))
                s = fromEnv.Trim();
            else
                s = WithDatabase(fromEnv.Trim(),
                    OperatingSystem.IsWindows() ? database : ResolveLinuxDatabaseName(database));
        }
        else
        {
            var dbName = OperatingSystem.IsWindows()
                ? database
                : ResolveLinuxDatabaseName(database);
            if (OperatingSystem.IsWindows())
            {
                var host = TryGetProcessOrMachineSetting("LAPLACE_PGHOST") ?? "localhost";
                var port = TryGetProcessOrMachineSetting("LAPLACE_PGPORT") ?? "5432";
                var user = TryGetProcessOrMachineSetting("LAPLACE_PGUSER")
                           ?? TryGetProcessOrMachineSetting("LAPLACE_ROLE")
                           ?? "laplace";
                s = $"Host={host};Port={port};Username={user};Database={dbName};Command Timeout=0";
            }
            else
            {
                s = $"Host=/var/run/postgresql;Username=laplace_admin;Database={dbName}";
            }
        }

        return EnsurePostgresConnectionDefaults(s);
    }

    private static bool HasDatabase(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(part[..eq].Trim(), "Database", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(part[(eq + 1)..]))
                return true;
        }
        return false;
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

    /// <summary>
    /// Mask the password in a connection string for safe logging. Covers both the
    /// `Password=` and `Pwd=` spellings.
    /// </summary>
    public static string RedactConnectionString(string conn)
    {
        if (string.IsNullOrEmpty(conn)) return conn;
        try
        {
            var parsed = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = conn };
            foreach (var key in parsed.Keys.Cast<string>().ToArray())
                if (key.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("pwd", StringComparison.OrdinalIgnoreCase)) parsed[key] = "***";
            return parsed.ConnectionString;
        }
        catch (ArgumentException)
        {
            // A malformed quoted password cannot safely be redacted with a regex.
            return "(invalid connection string; contents withheld)";
        }
    }

    public static string ResolveT0Perfcache()
    {
        var fromEnv = TryGetProcessOrMachineSetting("LAPLACE_PERFCACHE_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var selected = Path.GetFullPath(fromEnv.Trim());
            if (!File.Exists(selected)) throw new FileNotFoundException("The selected LAPLACE_PERFCACHE_BIN does not exist.", selected);
            return selected;
        }

        if (TryResolveEngineBuildRoot(out var engineBuild))
        {
            var fromBuild = Path.Combine(engineBuild, "core", "perfcache", "laplace_t0_perfcache.bin");
            if (File.Exists(fromBuild)) return fromBuild;
            // Sorted so a build tree holding more than one matching blob resolves the same
            // codepoint table on every run, independent of filesystem listing order.
            foreach (var hit in Directory.EnumerateFiles(engineBuild, "laplace_t0_perfcache.bin", SearchOption.AllDirectories)
                                         .OrderBy(static p => p, StringComparer.Ordinal))
                return hit;
        }

        var installPrefix = TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX");
        var share = string.IsNullOrWhiteSpace(installPrefix) ? Path.Combine(InstallRoot, "share", "laplace")
            : Path.Combine(installPrefix.Trim(), "share", "laplace");
        if (Directory.Exists(share))
        {
            var hit = Directory.EnumerateFiles(share, "laplace_t0_perfcache*.bin")
                               .OrderBy(static p => p, StringComparer.Ordinal).FirstOrDefault();
            if (hit is not null) return hit;
        }

        // The error names exactly the locations this resolution probed.
        throw new InvalidOperationException(
            "T0 perfcache not found. Searched: "
            + $"LAPLACE_PERFCACHE_BIN={TryGetProcessOrMachineSetting("LAPLACE_PERFCACHE_BIN") ?? "(unset)"}; "
            + (TryResolveEngineBuildRoot(out var probed)
                ? $"engine build root '{probed}' (core/perfcache/ and recursive)"
                : "no engine build root resolved from AppContext.BaseDirectory or cwd")
            + $"; {share}. Build the engine, or set LAPLACE_PERFCACHE_BIN to the blob.");
    }

    public static string ResolveIngestRoot()
    {
        var configured = TryGetProcessOrMachineSetting("LAPLACE_DATA");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Declare LAPLACE_DATA as the ingest corpus directory in the installation configuration.");
        return ExistingDirectory(configured, "LAPLACE_DATA");
    }

    private static string ExistingDirectory(string value, string setting)
    {
        var path = Path.GetFullPath(value.Trim());
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"The selected {setting} directory does not exist: {path}");
        return path;
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
            foreach (var hit in Directory.EnumerateFiles(engineBuild, "laplace_highway_perfcache.bin", SearchOption.AllDirectories)
                                         .OrderBy(static p => p, StringComparer.Ordinal))
                return hit;
        }

        throw new InvalidOperationException(
            "Highway perfcache not found — build the engine (laplace_highway_perfcache.bin beside T0 blob).");
    }

    public static string ResolveModelHub()
    {
        var cache = Environment.GetEnvironmentVariable("HF_HUB_CACHE");
        if (!string.IsNullOrWhiteSpace(cache)) return ExistingDirectory(cache, "HF_HUB_CACHE");
        var home = Environment.GetEnvironmentVariable("HF_HOME");
        if (!string.IsNullOrWhiteSpace(home)) return ExistingDirectory(Path.Combine(home.Trim(), "hub"), "HF_HOME/hub");
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var cacheRoot = !string.IsNullOrWhiteSpace(xdg) ? xdg.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return ExistingDirectory(Path.Combine(cacheRoot, "huggingface", "hub"), "Hugging Face cache (or set HF_HUB_CACHE)");
    }

    public static string ResolveGgufOutputDir()
    {
        var outRoot = TryGetProcessOrMachineSetting("LAPLACE_OUT");
        if (!string.IsNullOrWhiteSpace(outRoot))
            return Path.GetFullPath(Path.Combine(outRoot.Trim(), "models"));

        var buildRoot = TryGetProcessOrMachineSetting("LAPLACE_BUILD_ROOT");
        if (!string.IsNullOrWhiteSpace(buildRoot))
            return Path.GetFullPath(Path.Combine(buildRoot.Trim(), "out", "models"));

        if (TryDefaultBuildRoot(out buildRoot))
            return Path.GetFullPath(Path.Combine(buildRoot, "out", "models"));

        throw new InvalidOperationException("Declare LAPLACE_OUT for model output; installed runtimes do not write into build or binary directories.");
    }

    /// <summary>Process environment first, then <c>deploy/secrets/{secretFile}</c>.</summary>
    public static string? TryReadConfig(string key, string? secretFile = null)
    {
        var fromEnv = TryGetProcessOrMachineSetting(key);
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

    /// <summary>
    /// Reads a setting from the process environment first, falling back to the machine-level
    /// installation declaration (<c>C:\ProgramData\Laplace\machine.env</c> on Windows,
    /// <c>/etc/laplace/machine.env</c> on Linux, or the file named by <c>LAPLACE_MACHINE_ENV</c>).
    /// </summary>
    public static string? TryGetProcessOrMachineSetting(string key)
    {
        var fromEnv = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        var machineEnvPath = Environment.GetEnvironmentVariable("LAPLACE_MACHINE_ENV");
        if (string.IsNullOrWhiteSpace(machineEnvPath))
        {
            machineEnvPath = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Laplace", "machine.env")
                : "/etc/laplace/machine.env";
        }
        else
        {
            machineEnvPath = machineEnvPath.Trim();
            if (!File.Exists(machineEnvPath))
                return null;
        }

        if (!File.Exists(machineEnvPath))
            return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(machineEnvPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var k = line[..eq].Trim();
            var v = line[(eq + 1)..].Trim();
            if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
                v = v[1..^1];
            v = Environment.ExpandEnvironmentVariables(v);
            v = Regex.Replace(v, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)|%([A-Za-z_][A-Za-z0-9_]*)%", m =>
            {
                var refKey = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                return Environment.GetEnvironmentVariable(refKey)
                       ?? (values.TryGetValue(refKey, out var refVal) ? refVal : m.Value);
            });
            values[k] = v;
        }

        return values.TryGetValue(key, out var result) && !string.IsNullOrWhiteSpace(result)
            ? result.Trim()
            : null;
    }

    private static string? TryStampedRepoRoot()
        => typeof(LaplaceInstall).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LaplaceRepoRoot")?.Value;

    private static bool TryResolveEngineBuildRoot(out string engineBuild)
    {
        var fromEnv = TryGetProcessOrMachineSetting("LAPLACE_ENGINE_BUILD");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            engineBuild = Path.GetFullPath(fromEnv.Trim());
            if (Directory.Exists(engineBuild)) return true;
        }

        fromEnv = TryGetProcessOrMachineSetting("LAPLACE_BUILD_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            engineBuild = Path.GetFullPath(Path.Combine(fromEnv.Trim(), "build-win"));
            if (Directory.Exists(engineBuild)) return true;
        }

        if (TryDefaultBuildRoot(out var buildRoot))
        {
            var candidate = Path.GetFullPath(Path.Combine(buildRoot, OperatingSystem.IsWindows() ? "build-win" : "engine"));
            if (Directory.Exists(candidate))
            {
                engineBuild = candidate;
                return true;
            }
            candidate = Path.GetFullPath(Path.Combine(buildRoot, "build-win"));
            if (Directory.Exists(candidate))
            {
                engineBuild = candidate;
                return true;
            }
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
        // Install-prefix secrets drop (Linux): setup-host seeds
        // /opt/laplace/secrets/{fileName} from ~/.config/shell/secrets.env.
        var prefix = Environment.GetEnvironmentVariable("LAPLACE_INSTALL_PREFIX");
        if (string.IsNullOrWhiteSpace(prefix))
            prefix = OperatingSystem.IsWindows() ? null : "/opt/laplace";
        if (!string.IsNullOrWhiteSpace(prefix))
            yield return Path.Combine(prefix.Trim(), "secrets", fileName);

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
    /// The default database is laplace; PGDATABASE overrides the default, and any other
    /// explicit name is used as-is.
    /// </summary>
    private static string ResolveLinuxDatabaseName(string database)
    {
        if (database != "laplace")
            return database;

        var fromEnv = Environment.GetEnvironmentVariable("PGDATABASE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        return "laplace";
    }
}
