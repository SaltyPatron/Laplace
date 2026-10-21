using System.Diagnostics;
using Laplace.Chess.Uci.Lab;
using Xunit;

namespace Laplace.Chess.Tests.Uci;

public sealed class ProcessToolRunnerFailureTests
{
    [Fact]
    public void CapturesBothStreamsAndClosesChildInput()
    {
        var run = Shell(
            "if ($null -ne [Console]::ReadLine()) { exit 9 }; [Console]::Out.Write('tool-stdout'); [Console]::Error.Write('tool-stderr'); exit 23",
            "read value && exit 9; printf tool-stdout; printf tool-stderr >&2; exit 23");
        var result = ProcessToolRunner.Instance.Run(run);
        Assert.Equal(23, result.ExitCode);
        Assert.Equal("tool-stdout", result.Stdout);
        Assert.Equal("tool-stderr", result.Stderr);
    }

    [Fact]
    public void TranscriptFailurePropagatesAndTheActualChildHasExited()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), "laplace-tool-pid-" + Guid.NewGuid().ToString("N"));
        var failure = new IOException("transcript write fixture failed");
        try
        {
            var run = BlockingChild(pidFile);
            var observed = Assert.Throws<IOException>(() =>
                ProcessToolRunner.Instance.Run(run, new FailedWriter(failure)));
            Assert.Same(failure, observed);
            AssertChildExited(pidFile);
        }
        finally { File.Delete(pidFile); }
    }

    [Fact]
    public void DeadlineStopsTheActualChildBeforeReturning()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), "laplace-tool-pid-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<TimeoutException>(() => ProcessToolRunner.Instance.Run(
                BlockingChild(pidFile) with { Timeout = TimeSpan.FromSeconds(2) }));
            AssertChildExited(pidFile);
        }
        finally { File.Delete(pidFile); }
    }

    private static ToolRun BlockingChild(string pidFile)
        => Shell(
            "[IO.File]::WriteAllText($env:LAPLACE_TOOL_TEST_PID, [string]$PID); [Console]::Out.Write('transcript-fixture'); Start-Sleep -Seconds 30",
            "echo $$ > \"$LAPLACE_TOOL_TEST_PID\"; printf transcript-fixture; sleep 30") with
        { Environment = new Dictionary<string, string?> { ["LAPLACE_TOOL_TEST_PID"] = pidFile } };

    private static void AssertChildExited(string pidFile)
    {
        Assert.True(File.Exists(pidFile));
        int pid = int.Parse(File.ReadAllText(pidFile));
        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.HasExited, $"tool process {pid} remains active");
        }
        catch (ArgumentException) { /* The exited process has already been reaped. */ }
    }

    private static ToolRun Shell(string windows, string unix)
        => OperatingSystem.IsWindows()
            ? new("pwsh", ["-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(windows))], Timeout: TimeSpan.FromSeconds(15))
            : new("/bin/sh", ["-c", unix], Timeout: TimeSpan.FromSeconds(15));

    private sealed class FailedWriter(Exception failure) : StringWriter
    {
        public override void Write(string? value) => throw failure;
    }
}
