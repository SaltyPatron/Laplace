using System.Collections.Concurrent;
using System.Diagnostics;
using Laplace.Ops;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Laplace.Core.Tests;

[Collection("Persistent logging")]
public sealed class CapturedProcessTests
{
    internal static ProcessStartInfo Shell(string windows, string unix)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh");
        foreach (var arg in OperatingSystem.IsWindows()
                     ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", windows }
                     : new[] { "-c", unix }) start.ArgumentList.Add(arg);
        return start;
    }

    [Fact]
    public async Task BothPipesDrainWithoutUnboundedDiagnosticMemoryAndRetainExitFailure()
    {
        using var child = CapturedProcess.Start(Shell(
            "[Console]::Out.Write(('o' * 100000) + 'stdout-end'); [Console]::Error.Write(('e' * 100000) + 'stderr-end'); exit 23",
            "head -c 100000 /dev/zero | tr '\\0' o; printf stdout-end; head -c 100000 /dev/zero | tr '\\0' e >&2; printf stderr-end >&2; exit 23"), 4096);
        var result = await child.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(23, result.ExitCode);
        Assert.False(result.OutputIncomplete);
        Assert.EndsWith("stdout-end", result.Stdout);
        Assert.EndsWith("stderr-end", result.Stderr);
        Assert.InRange(result.Stdout.Length, 4096, 4200);
        Assert.InRange(result.Stderr.Length, 4096, 4200);
    }

    [Fact]
    public async Task ChildFailureHasTerminalReceiptAndCorrelatedErrorAfterLeavingActiveList()
    {
        var previous = Log.Logger;
        var sink = new Events();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = logger;
        try
        {
            IngestProcessRunner.StartReceipt started;
            using (LogContext.PushProperty("CorrelationId", "ingest-child-regression"))
                started = IngestProcessRunner.Start(Shell(
                    "[Console]::Error.WriteLine('loader-fixture-failed'); exit 23",
                    "echo loader-fixture-failed >&2; exit 23"), "process-fixture");
            var ended = await WaitForCompletion(started.ProcessRunId);
            Assert.Equal("failed", ended.Status);
            Assert.Equal(23, ended.ExitCode);
            Assert.DoesNotContain(IngestProcessRunner.ActiveProcesses(), x => x.ProcessRunId == started.ProcessRunId);
            var terminal = Assert.Single(sink.Rows, x => x.MessageTemplate.Text.StartsWith("INGEST_CHILD_EXITED", StringComparison.Ordinal));
            Assert.Equal(LogEventLevel.Error, terminal.Level);
            Assert.Equal("\"ingest-child-regression\"", terminal.Properties["CorrelationId"].ToString());
            Assert.Contains("loader-fixture-failed", terminal.Properties["ChildStderr"].ToString());
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public async Task StopConfirmsChildTerminationAndRetainsStoppedReceipt()
    {
        var started = IngestProcessRunner.Start(Shell("Start-Sleep -Seconds 60", "sleep 60"), "stop-fixture");
        try
        {
            var stopped = IngestProcessRunner.Stop(started.ProcessId);
            Assert.True(stopped.StopRequested);
            var ended = await WaitForCompletion(started.ProcessRunId);
            Assert.Equal("stopped", ended.Status);
            Assert.NotEqual(0, ended.ExitCode);
        }
        finally { IngestProcessRunner.Stop(started.ProcessId); }
    }

    [Fact]
    public async Task OutputObserverFailureTerminatesChildAndRemainsAnException()
    {
        using var child = CapturedProcess.Start(Shell(
            "[Console]::Out.WriteLine('ready'); Start-Sleep -Seconds 60",
            "echo ready; sleep 60"), output: (_, _) => throw new IOException("capture-fixture-failed"));
        var failure = await Assert.ThrowsAsync<IOException>(async () =>
            await child.Completion.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("capture-fixture-failed", failure.Message);
        Assert.True(Exited(child.Id));
    }

    private static async Task<IngestProcessRunner.CompletionReceipt> WaitForCompletion(Guid run)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var found = IngestProcessRunner.CompletedProcesses().SingleOrDefault(x => x.Start.ProcessRunId == run);
            if (found is not null) return found;
            await Task.Delay(20, deadline.Token);
        }
    }

    internal static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private sealed class Events : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Rows { get; } = new();
        public void Emit(LogEvent logEvent) => Rows.Enqueue(logEvent);
    }
}
