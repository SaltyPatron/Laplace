using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Laplace.Ops;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Laplace.Endpoints.Mcp.Tests;

[CollectionDefinition("MCP process diagnostics", DisableParallelization = true)]
public sealed class McpProcessDiagnosticCollection;

[Collection("MCP process diagnostics")]
public sealed class IngestProcessCommandTests
{
    [Fact]
    public void ChildFailureReturnsTheObservedExitAndBothPipesWithItsRunId()
    {
        var previous = Log.Logger;
        var events = new CapturedEvents();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(events).CreateLogger();
        Log.Logger = logger;
        IngestProcessRunner.StartReceipt? started = null;
        try
        {
            var response = IngestProcessCommand.Run("child-fixture", "input-fixture", 10, () => started =
                IngestProcessRunner.Start(Shell("[Console]::Out.WriteLine('child-out'); [Console]::Error.WriteLine('child-cause'); exit 23",
                    "echo child-out; echo child-cause >&2; exit 23"), "child-fixture"));
            Assert.True(response.IsError);
            using var json = JsonDocument.Parse(response.Content);
            var row = json.RootElement.GetProperty("rows")[0];
            Assert.Equal(23, row.GetProperty("exit_code").GetInt32());
            Assert.Equal("failed", row.GetProperty("status").GetString());
            Assert.False(row.GetProperty("timed_out").GetBoolean());
            Assert.Equal(started!.ProcessRunId.ToString(), row.GetProperty("process_run_id").GetString());
            Assert.Contains("child-out", row.GetProperty("stdout").GetString());
            Assert.Contains("child-cause", row.GetProperty("stderr").GetString());
            var diagnostic = Assert.Single(events.Rows, row => row.MessageTemplate.Text.StartsWith("INGEST_CHILD_EXITED", StringComparison.Ordinal));
            Assert.Equal(LogEventLevel.Error, diagnostic.Level);
            Assert.Contains("child-cause", diagnostic.RenderMessage());
            Assert.True(Exited(started.ProcessId));
        }
        finally { if (started is not null) IngestProcessRunner.Stop(started.ProcessId); Log.Logger = previous; }
    }

    [Fact]
    public void TimeoutReturnsOnlyAfterTheOwnedChildActuallyStops()
    {
        IngestProcessRunner.StartReceipt? started = null;
        try
        {
            var response = IngestProcessCommand.Run("timeout-fixture", "input-fixture", 1, () => started =
                IngestProcessRunner.Start(Shell("[Console]::Out.WriteLine('before-timeout'); Start-Sleep -Seconds 60",
                    "echo before-timeout; sleep 60"), "timeout-fixture"));
            Assert.True(response.IsError);
            using var json = JsonDocument.Parse(response.Content);
            var row = json.RootElement.GetProperty("rows")[0];
            Assert.True(row.GetProperty("timed_out").GetBoolean());
            Assert.Equal("stopped", row.GetProperty("status").GetString());
            Assert.Contains("before-timeout", row.GetProperty("stdout").GetString());
            Assert.NotEqual(0, row.GetProperty("exit_code").GetInt32());
            Assert.True(Exited(started!.ProcessId));
        }
        finally { if (started is not null) IngestProcessRunner.Stop(started.ProcessId); }
    }

    [Fact]
    public async Task LargeOutputIsBoundedAndLateWaitersReadTheSameTerminalReceipt()
    {
        IngestProcessRunner.StartReceipt? started = null;
        try
        {
            var response = IngestProcessCommand.Run("large-fixture", "input-fixture", 10, () => started =
                IngestProcessRunner.Start(Shell("[Console]::Out.Write(('o'*100000)+'stdout-end'); [Console]::Error.Write(('e'*100000)+'stderr-end')",
                    "head -c 100000 /dev/zero | tr '\\0' o; printf stdout-end; head -c 100000 /dev/zero | tr '\\0' e >&2; printf stderr-end >&2"), "large-fixture"));
            Assert.False(response.IsError);
            using var json = JsonDocument.Parse(response.Content);
            var row = json.RootElement.GetProperty("rows")[0];
            Assert.EndsWith("stdout-end", row.GetProperty("stdout").GetString());
            Assert.EndsWith("stderr-end", row.GetProperty("stderr").GetString());
            Assert.InRange(row.GetProperty("stdout").GetString()!.Length, 4000, 4050);
            Assert.InRange(row.GetProperty("stderr").GetString()!.Length, 4000, 4050);
            var ended = await IngestProcessRunner.WaitForCompletionAsync(started!.ProcessRunId);
            Assert.InRange(ended.Stdout.Length, 16000, 16500);
            Assert.InRange(ended.Stderr.Length, 16000, 16500);
            Assert.DoesNotContain(IngestProcessRunner.ActiveProcesses(), item => item.ProcessRunId == started.ProcessRunId);
        }
        finally { if (started is not null) IngestProcessRunner.Stop(started.ProcessId); }
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotLoseOwnershipOfTheRunningChild()
    {
        var started = IngestProcessRunner.Start(Shell("Start-Sleep -Seconds 60", "sleep 60"), "wait-fixture");
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                IngestProcessRunner.WaitForCompletionAsync(started.ProcessRunId, cancelled.Token));
            Assert.Contains(IngestProcessRunner.ActiveProcesses(), item => item.ProcessRunId == started.ProcessRunId);
            Assert.True(IngestProcessRunner.Stop(started.ProcessId).StopRequested);
            var ended = await IngestProcessRunner.WaitForCompletionAsync(started.ProcessRunId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("stopped", ended.Status);
            Assert.True(Exited(started.ProcessId));
        }
        finally { IngestProcessRunner.Stop(started.ProcessId); }
    }

    private static ProcessStartInfo Shell(string windows, string unix)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh");
        foreach (var argument in OperatingSystem.IsWindows()
                     ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", windows }
                     : new[] { "-c", unix }) start.ArgumentList.Add(argument);
        return start;
    }

    private static bool Exited(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private sealed class CapturedEvents : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Rows { get; } = new();
        public void Emit(LogEvent logEvent) => Rows.Enqueue(logEvent);
    }
}
