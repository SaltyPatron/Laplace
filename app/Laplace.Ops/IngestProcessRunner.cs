using System.Collections.Concurrent;
using System.Diagnostics;
using Laplace.Engine.Core;

namespace Laplace.Ops;

/// <summary>
/// Starts the canonical <c>Laplace.Cli ingest</c> lane without teaching every
/// operator surface a second source registry. The CLI remains the authority for
/// source names, defaults, argument validation and ingest semantics.
///
/// Processes started by this host are retained only while they are alive so the
/// operator surface can stop the actual CLI process rather than merely rewriting
/// a journal receipt. We intentionally never attach to arbitrary PIDs: that avoids
/// PID-reuse races and keeps process control scoped to children this host started.
/// </summary>
public static class IngestProcessRunner
{
    private static readonly ConcurrentDictionary<int, Process> OwnedProcesses = new();

    public sealed record StartReceipt(
        int ProcessId,
        string Source,
        string? Path,
        string CliPath,
        IReadOnlyList<string> Arguments);

    public sealed record StopReceipt(
        int ProcessId,
        bool Found,
        bool WasRunning,
        bool StopRequested);

    public static StartReceipt Start(
        string source,
        string? path = null,
        IReadOnlyList<string>? extraArguments = null)
    {
        source = (source ?? string.Empty).Trim();
        path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (source.Length == 0)
            throw new ArgumentException("Ingest source is required.", nameof(source));
        if (source.Any(char.IsControl))
            throw new ArgumentException("Ingest source contains control characters.", nameof(source));
        if (path is not null && !File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException($"Ingest path not found: {path}", path);

        var cliPath = ResolveCliBinary()
            ?? throw new FileNotFoundException(
                "Laplace.Cli binary not found. Set LAPLACE_CLI_BIN or install/build the canonical CLI.");

        var args = new List<string> { "ingest", source };
        if (path is not null) args.Add(path);
        if (extraArguments is not null)
        {
            foreach (var value in extraArguments)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value.Any(char.IsControl))
                    throw new ArgumentException("An ingest argument contains control characters.", nameof(extraArguments));
                args.Add(value);
            }
        }

        var start = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var value in args) start.ArgumentList.Add(value);

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Laplace.Cli ingest process did not start.");
        var pid = process.Id;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => ReleaseOwnedProcess(pid);

        if (!OwnedProcesses.TryAdd(pid, process))
        {
            process.Dispose();
            throw new InvalidOperationException($"Ingest process {pid} could not be registered for operator control.");
        }

        // The process can exit between Process.Start and event registration. Close that
        // race immediately; ReleaseOwnedProcess is idempotent.
        if (process.HasExited)
            ReleaseOwnedProcess(pid);

        return new StartReceipt(pid, source, path, cliPath, args);
    }

    public static StopReceipt Stop(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");

        if (!OwnedProcesses.TryRemove(processId, out var process))
            return new StopReceipt(processId, Found: false, WasRunning: false, StopRequested: false);

        try
        {
            if (process.HasExited)
                return new StopReceipt(processId, Found: true, WasRunning: false, StopRequested: false);

            // Kill the process tree because the CLI may own workers whose continued
            // writes would make a journal-only cancellation actively misleading.
            process.Kill(entireProcessTree: true);
            return new StopReceipt(processId, Found: true, WasRunning: true, StopRequested: true);
        }
        catch (InvalidOperationException)
        {
            return new StopReceipt(processId, Found: true, WasRunning: false, StopRequested: false);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void ReleaseOwnedProcess(int processId)
    {
        if (OwnedProcesses.TryRemove(processId, out var process))
            process.Dispose();
    }

    public static string? ResolveCliBinary()
    {
        var fromEnv = Environment.GetEnvironmentVariable("LAPLACE_CLI_BIN");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        if (!LaplaceInstall.TryRepoRoot(out var root)) return null;
        var exeName = OperatingSystem.IsWindows() ? "Laplace.Cli.exe" : "Laplace.Cli";
        foreach (var candidate in Candidates(root, exeName))
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static IEnumerable<string> Candidates(string root, string exeName)
    {
        // ReadyToRun is the production ingest binary when present; normal build output
        // remains a development fallback. Keep discovery here so MCP/HTTP/other operator
        // clients cannot drift into different binaries.
        foreach (var config in new[] { "Release", "Debug" })
        {
            yield return Path.Combine(root, "app", "bin", "Laplace.Cli", config, "net10.0-r2r", exeName);
            yield return Path.Combine(root, "app", "Laplace.Cli", "bin", config, "net10.0", exeName);
        }
    }
}
