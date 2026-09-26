using System.Collections.Concurrent;
using System.Diagnostics;
using Laplace.Engine.Core;

namespace Laplace.Ops;

/// <summary>
/// Starts <c>Laplace.Cli ingest</c> as a child process, so every interface drives the one
/// ingest recipe and the CLI stays the authority for source names, defaults and argument
/// validation.
///
/// Children this host started are retained while alive, so a caller can find and stop the
/// real process later. It never attaches to arbitrary PIDs, which avoids PID-reuse races.
/// </summary>
public static class IngestProcessRunner
{
    private sealed record OwnedProcess(Process Process, StartReceipt Receipt);
    private static readonly ConcurrentDictionary<int, OwnedProcess> OwnedProcesses = new();

    public sealed record StartReceipt(
        int ProcessId,
        string Source,
        string? Path,
        string CliPath,
        IReadOnlyList<string> Arguments,
        DateTimeOffset StartedAt);

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
        var receipt = new StartReceipt(pid, source, path, cliPath, args, DateTimeOffset.UtcNow);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => ReleaseOwnedProcess(pid);

        if (!OwnedProcesses.TryAdd(pid, new OwnedProcess(process, receipt)))
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
            throw new InvalidOperationException($"Ingest process {pid} could not be registered for operator control.");
        }

        // The process can exit between Process.Start and event registration. Close that
        // race immediately; ObjectDisposedException derives from InvalidOperationException,
        // so this one catch covers both the exited/disposed handle races.
        try
        {
            if (process.HasExited)
                ReleaseOwnedProcess(pid);
        }
        catch (InvalidOperationException) { }

        return receipt;
    }

    public static IReadOnlyList<StartReceipt> ActiveProcesses()
    {
        var active = new List<StartReceipt>();
        foreach (var pair in OwnedProcesses.ToArray())
        {
            try
            {
                if (pair.Value.Process.HasExited)
                {
                    ReleaseOwnedProcess(pair.Key);
                    continue;
                }
                active.Add(pair.Value.Receipt);
            }
            catch (InvalidOperationException)
            {
                // Includes ObjectDisposedException: both mean the cached handle is no
                // longer a live process we can safely expose or control.
                ReleaseOwnedProcess(pair.Key);
            }
        }
        active.Sort(static (left, right) => right.StartedAt.CompareTo(left.StartedAt));
        return active;
    }

    public static StopReceipt Stop(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");

        if (!OwnedProcesses.TryRemove(processId, out var owned))
            return new StopReceipt(processId, Found: false, WasRunning: false, StopRequested: false);

        var process = owned.Process;
        try
        {
            if (process.HasExited)
                return new StopReceipt(processId, Found: true, WasRunning: false, StopRequested: false);

            // Kill the whole tree: the CLI's workers would otherwise keep writing.
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
        if (OwnedProcesses.TryRemove(processId, out var owned))
            owned.Process.Dispose();
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
        // ReadyToRun output first, then ordinary build output; every caller resolves
        // the binary through this one list.
        foreach (var config in new[] { "Release", "Debug" })
        {
            yield return Path.Combine(root, "app", "bin", "Laplace.Cli", config, "net10.0-r2r", exeName);
            yield return Path.Combine(root, "app", "Laplace.Cli", "bin", config, "net10.0", exeName);
        }
    }
}
