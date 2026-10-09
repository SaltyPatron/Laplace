using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Laplace.Engine.Core;

namespace Laplace.Chess.Uci.Lab;

/// <summary>
/// The chess lab's installed settings, read the way the application reads them (ChessRuntimeConfiguration): the
/// selected keys from the service's installed environment, then the chess-lab files, then the repository's
/// deploy/secrets/chess-lab.env, the first file with a value winning and the last assignment within a file winning;
/// the process environment overrides all of them. Only the named keys are ever collected, so a token in the same files
/// never enters a report.
/// </summary>
public static class ChessLabConfig
{
    public static readonly IReadOnlySet<string> Keys = BuildKeys();

    private static HashSet<string> BuildKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            "LAPLACE_STOCKFISH", "LAPLACE_CUTECHESS", "LAPLACE_CUTECHESS_GUI", "LAPLACE_CUTECHESS_GUI_RECEIPT", "LAPLACE_SYZYGY",
            "LAPLACE_CHESS_OPENINGS", "LAPLACE_DATA_ROOT", "LAPLACE_EXTERNAL", "LAPLACE_STOCKFISH_SOURCE", "LAPLACE_CUTECHESS_BUILD",
            "LAPLACE_QT_BIN", "LAPLACE_CHESS_LAB_DIR", "LAPLACE_ZSTD_LIBRARY", "LAPLACE_ZSTD_WINDOW_LOG_MAX", "LAPLACE_ZSTD_SOURCE",
            "LAPLACE_ZSTD_BUILD", "LAPLACE_FASTCHESS", "LAPLACE_ORDO", "LAPLACE_LC0", "LAPLACE_LC0_NET", "LAPLACE_LC0_BACKEND",
            "LAPLACE_CHESS_BOOKS",
        };
        foreach (var suffix in new[] { "THREADS", "HASH_MB", "NUMA_POLICY", "SYZYGY_PATH", "FILE", "TIMEOUT_SECONDS", "PROCESSES" })
            keys.Add("LAPLACE_STOCKFISH_EVAL_" + suffix);
        return keys;
    }

    /// <summary>LAPLACE_INSTALL_PREFIX, else LAPLACE_TOOLS\chess on Windows and /opt/laplace elsewhere.</summary>
    public static string DefaultPrefix()
    {
        if (Environment.GetEnvironmentVariable("LAPLACE_INSTALL_PREFIX") is { Length: > 0 } prefix) return prefix;
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetEnvironmentVariable("LAPLACE_TOOLS") is { Length: > 0 } tools ? tools : "tools", "chess")
            : "/opt/laplace";
    }

    public static Dictionary<string, string> Read(string? prefix = null, IReadOnlySet<string>? keys = null,
        string? apiEnvironment = null, bool includeEnvironment = true, string? repoRoot = null)
    {
        prefix ??= DefaultPrefix();
        var allowed = keys ?? Keys;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string api = Path.Combine(prefix, "app", "laplace-api.env");
        var files = new List<string>
        {
            api, Path.Combine(prefix, "app", "chess-lab.env"), Path.Combine(prefix, "chess-lab.env"),
            Path.Combine(prefix, "secrets", "chess-lab.env"),
        };
        if ((repoRoot ?? RepoRoot()) is { } root) files.Add(Path.Combine(root, "deploy", "secrets", "chess-lab.env"));
        foreach (var path in files)
        {
            string? text = path == api && apiEnvironment is not null ? apiEnvironment : File.Exists(path) ? File.ReadAllText(path) : null;
            if (text is null) continue;
            var selected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.TrimStart().StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line[..eq].Trim();
                if (!allowed.Contains(key)) continue;
                string value = line[(eq + 1)..].Trim();
                if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'') value = value[1..^1];
                selected[key] = value;
            }
            foreach (var (key, value) in selected)
                if (value.Trim().Length > 0) result.TryAdd(key, value);
        }
        // The application's last resort (ChessRuntimeConfiguration): deploy/secrets/chess-lab.env beside the install.
        if (repoRoot is null)
            foreach (var key in allowed)
                if (!result.ContainsKey(key) && LaplaceInstall.TryReadDeploySecret("chess-lab.env", key) is { Length: > 0 } v)
                    result[key] = v.Length >= 2 && v[0] == v[^1] && v[0] is '"' or '\'' ? v[1..^1] : v;
        if (includeEnvironment)
        {
            foreach (var key in allowed)
                if (Environment.GetEnvironmentVariable(key) is { } v && v.Trim().Length > 0) result[key] = v.Trim();
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAPLACE_STOCKFISH_SOURCE"))
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LAPLACE_STOCKFISH")))
                result.Remove("LAPLACE_STOCKFISH");
        }
        return result;
    }

    public static string? RepoRoot() => LaplaceInstall.TryRepoRoot(out var root) ? root : null;

    /// <summary>LAPLACE_DATA_ROOT, else D:\Data\Ingest on Windows and /vault/Data elsewhere.</summary>
    public static string DataRoot(IReadOnlyDictionary<string, string> config)
        => config.TryGetValue("LAPLACE_DATA_ROOT", out var root) ? root : OperatingSystem.IsWindows() ? @"D:\Data\Ingest" : "/vault/Data";
}

/// <summary>The pinned release files (deploy/*.json): beside the binary when published, else the repository's.</summary>
public static class LabLocks
{
    public static JsonNode Read(string repoRelative)
    {
        string name = Path.GetFileName(repoRelative);
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, name) };
        if (ChessLabConfig.RepoRoot() is { } root) candidates.Add(Path.Combine(root, repoRelative));
        foreach (var path in candidates)
            if (File.Exists(path)) return JsonNode.Parse(File.ReadAllText(path))!;
        throw new FileNotFoundException($"{repoRelative} is neither beside laplace-uci nor in the repository");
    }

    public static JsonNode Ladder => Read("deploy/chess-ladder-release.json");
    public static JsonNode Cutechess => Read("deploy/cutechess-release.json");
    public static JsonNode Stockfish => Read("deploy/linux/stockfish-release.json");
    public static JsonNode StockfishProfiles => Read("deploy/stockfish-profiles.json");
    public static JsonNode Zstd => Read("deploy/zstd-release.json");
}

public static class LabFiles
{
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>An executable on the PATH, or null.</summary>
    public static string? Which(string name)
    {
        string[] names = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? [name + ".exe", name] : [name];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
            {
                string candidate = Path.Combine(dir.Trim('"'), n);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}

/// <summary>One external tool run: argv, working directory, environment overrides, a deadline, and optionally a file that
/// receives stdout and stderr together (fastchess's transcript).</summary>
public sealed record ToolRun(string Exe, IReadOnlyList<string> Args, string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null, TimeSpan? Timeout = null, string? TranscriptFile = null);

public sealed record ToolResult(int ExitCode, string Stdout, string Stderr)
{
    public string Output => Stdout + Stderr;
}

/// <summary>The seam every command runs external tools through, so tests can stand in for fastchess, Ordo or a probe.</summary>
public interface IToolRunner
{
    ToolResult Run(ToolRun run);
}

/// <summary>Runs a tool as a hidden child process (never a console window), stdin closed, output captured.</summary>
public sealed class ProcessToolRunner : IToolRunner
{
    public static readonly ProcessToolRunner Instance = new();

    public ToolResult Run(ToolRun run)
    {
        var psi = new ProcessStartInfo(run.Exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = run.WorkingDirectory ?? "",
        };
        foreach (var a in run.Args) psi.ArgumentList.Add(a);
        if (run.Environment is not null)
            foreach (var (k, v) in run.Environment)
                if (v is null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {run.Exe}");
        process.StandardInput.Close();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var transcript = run.TranscriptFile is null ? null : new StreamWriter(run.TranscriptFile, false, new UTF8Encoding(false));
        var gate = new object();
        void Sink(StringBuilder target, string? line)
        {
            if (line is null) return;
            lock (gate)
            {
                if (transcript is not null) transcript.WriteLine(line);
                else target.AppendLine(line);
            }
        }
        process.OutputDataReceived += (_, e) => Sink(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Sink(stderr, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            if (run.Timeout is { } timeout && !process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new TimeoutException($"{Path.GetFileName(run.Exe)} did not finish within {timeout.TotalSeconds:F0} s");
            }
            process.WaitForExit();
        }
        finally
        {
            lock (gate) transcript?.Dispose();
        }
        return new ToolResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
