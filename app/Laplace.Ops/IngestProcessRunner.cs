using System.Collections.Concurrent;
using System.Diagnostics;
using Laplace.Engine.Core;
using Serilog;
using Serilog.Context;

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
    private sealed class OwnedProcess(CapturedProcess process, StartReceipt receipt, Serilog.ILogger log)
    {
        public CapturedProcess Process { get; } = process;
        public StartReceipt Receipt { get; } = receipt;
        public Serilog.ILogger Log { get; } = log;
        public int StopRequested;
    }
    private static readonly ConcurrentDictionary<int, OwnedProcess> OwnedProcesses = new();
    private static readonly ConcurrentQueue<CompletionReceipt> Completed = new();

    public sealed record StartReceipt(
        int ProcessId,
        string Source,
        string? Path,
        string CliPath,
        IReadOnlyList<string> Arguments,
        DateTimeOffset StartedAt)
    {
        public Guid ProcessRunId { get; init; } = Guid.NewGuid();
    }

    public sealed record CompletionReceipt(StartReceipt Start, DateTimeOffset EndedAt,
        string Status, int? ExitCode, bool OutputIncomplete);

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
                "Laplace.Cli binary not found. Publish the CLI at LAPLACE_CLI_EXE or install the release's cli directory.");

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

        return Start(start, source, path, args);
    }

    internal static StartReceipt Start(ProcessStartInfo start, string source, string? path = null,
        IReadOnlyList<string>? arguments = null)
    {
        var runId = Guid.NewGuid();
        // Clone request context now; completion and stop can run after the request ends.
        var log = Log.ForContext(LogContext.Clone()).ForContext("SourceContext", typeof(IngestProcessRunner).FullName)
            .ForContext("ProcessRunId", runId).ForContext("Source", source);
        CapturedProcess process;
        try
        {
            process = CapturedProcess.Start(start, output: (stream, text) =>
            {
                log.Write(stream == "stderr" ? Serilog.Events.LogEventLevel.Warning
                    : Serilog.Events.LogEventLevel.Information,
                    "INGEST_CHILD_OUTPUT stream={ChildStream} {ChildOutput}", stream, text);
            });
        }
        catch (Exception ex)
        {
            log.Error(ex, "INGEST_CHILD_START_FAILED cli={CliPath}", start.FileName);
            throw;
        }
        var receipt = new StartReceipt(process.Id, source, path, start.FileName,
            arguments?.ToArray() ?? start.ArgumentList.ToArray(), DateTimeOffset.UtcNow) { ProcessRunId = runId };
        var owned = new OwnedProcess(process, receipt, log.ForContext("ChildProcessId", process.Id));
        if (!OwnedProcesses.TryAdd(process.Id, owned))
        {
            // Retain observation even if a previous exit's terminal receipt is being finalized.
            _ = ObserveAsync(owned);
            try { process.TerminateAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { owned.Log.Error(ex, "INGEST_CHILD_REGISTRATION_CLEANUP_FAILED"); }
            throw new InvalidOperationException($"Ingest process {process.Id} could not be registered for operator control.");
        }
        owned.Log.Information("INGEST_CHILD_STARTED cli={CliPath}", receipt.CliPath);
        _ = ObserveAsync(owned);
        return receipt;
    }

    public static IReadOnlyList<StartReceipt> ActiveProcesses() => OwnedProcesses.Values
        .Where(owned => !owned.Process.Completion.IsCompleted).Select(owned => owned.Receipt)
        .OrderByDescending(receipt => receipt.StartedAt).ToArray();

    public static IReadOnlyList<CompletionReceipt> CompletedProcesses() => Completed.ToArray()
        .OrderByDescending(receipt => receipt.EndedAt).ToArray();

    public static StopReceipt Stop(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");

        if (!OwnedProcesses.TryGetValue(processId, out var owned))
            return new StopReceipt(processId, Found: false, WasRunning: false, StopRequested: false);

        try
        {
            if (owned.Process.Completion.IsCompleted)
                return new StopReceipt(processId, Found: true, WasRunning: false, StopRequested: false);
            Interlocked.Exchange(ref owned.StopRequested, 1);
            owned.Process.TerminateAsync().GetAwaiter().GetResult();
            owned.Log.Information("INGEST_CHILD_STOP_CONFIRMED");
            return new StopReceipt(processId, Found: true, WasRunning: true, StopRequested: true);
        }
        catch (Exception ex)
        {
            // Keep ownership while alive so a failed termination remains observable and retryable.
            owned.Log.Error(ex, "INGEST_CHILD_STOP_FAILED");
            throw;
        }
    }

    private static async Task ObserveAsync(OwnedProcess owned)
    {
        CompletionReceipt completed;
        try
        {
            var result = await owned.Process.Completion.ConfigureAwait(false);
            string status = result.OutputIncomplete ? "observation_failed"
                : Volatile.Read(ref owned.StopRequested) != 0 ? "stopped"
                : result.ExitCode == 0 ? "completed" : "failed";
            completed = new(owned.Receipt, DateTimeOffset.UtcNow, status, result.ExitCode, result.OutputIncomplete);
            owned.Log.Write(status is "failed" or "observation_failed" ? Serilog.Events.LogEventLevel.Error
                : Serilog.Events.LogEventLevel.Information,
                "INGEST_CHILD_EXITED status={ChildStatus} exit_code={ExitCode} output_incomplete={OutputIncomplete} stderr={ChildStderr}",
                status, result.ExitCode, result.OutputIncomplete, result.Stderr);
        }
        catch (Exception ex)
        {
            owned.Log.Error(ex, "INGEST_CHILD_OBSERVATION_FAILED");
            completed = new(owned.Receipt, DateTimeOffset.UtcNow, "observation_failed", null, true);
        }
        Completed.Enqueue(completed);
        while (Completed.Count > 128) Completed.TryDequeue(out _);
        OwnedProcesses.TryRemove(new KeyValuePair<int, OwnedProcess>(owned.Receipt.ProcessId, owned));
        owned.Process.Dispose();
    }

    public static string? ResolveCliBinary()
    {
        // BIN is the existing explicit API override; EXE is the path selected by
        // the build/ingest scripts. A missing explicit selection must not silently
        // launch an older executable from a different checkout.
        var selected = Environment.GetEnvironmentVariable("LAPLACE_CLI_BIN");
        if (string.IsNullOrWhiteSpace(selected))
            selected = Environment.GetEnvironmentVariable("LAPLACE_CLI_EXE");
        return ResolveCliBinary(selected, LaplaceInstall.InstallRoot,
            LaplaceInstall.TryDefaultBuildRoot(out var build) ? build : null,
            LaplaceInstall.TryRepoRoot(out var repo) ? repo : null);
    }

    internal static string? ResolveCliBinary(string? selected, string installRoot, string? buildRoot, string? repoRoot)
    {
        if (!string.IsNullOrWhiteSpace(selected))
            return File.Exists(selected) ? Path.GetFullPath(selected) : null;
        var exeName = OperatingSystem.IsWindows() ? "Laplace.Cli.exe" : "Laplace.Cli";
        var installed = Path.Combine(installRoot, "cli", exeName);
        if (File.Exists(installed)) return Path.GetFullPath(installed);
        if (buildRoot is not null)
        {
            var published = Path.Combine(buildRoot, "app", "bin", "Laplace.Cli", "Release", "net10.0-r2r", exeName);
            if (File.Exists(published)) return Path.GetFullPath(published);
        }
        if (repoRoot is not null)
            foreach (var candidate in Candidates(repoRoot, exeName))
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
