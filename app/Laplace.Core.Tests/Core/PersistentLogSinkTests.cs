using System.Diagnostics;
using System.Text.Json;
using Laplace.Ops;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Extensions.Logging;
using Xunit;

namespace Laplace.Core.Tests;

[CollectionDefinition("Persistent logging", DisableParallelization = true)]
public sealed class PersistentLoggingCollection;

[Collection("Persistent logging")]
public sealed class PersistentLogSinkTests
{
    [Fact]
    public void TraceBridgeRetainsTypedExceptionAndCorrelationInRealPersistentEvents()
    {
        WithLogDirectory(directory =>
        {
            var previous = Log.Logger;
            using var logger = new LoggerConfiguration().ApplyLaplaceSinks("trace-test", false,
                health: new PersistentLogHealth()).CreateLogger();
            try
            {
                Log.Logger = logger;
                using (Serilog.Context.LogContext.PushProperty("CorrelationId", "isolated-input-request"))
                    Trace.TraceError("INPUT_FAILURE file={0}: {1}", "input-fixture",
                        new InvalidDataException("input-outer", new IOException("storage-inner")));
                string path = Assert.Single(Directory.GetFiles(directory, "laplace-trace-test-*.jsonl"));
                using var json = JsonDocument.Parse(ReadShared(path));
                Assert.Equal("Error", json.RootElement.GetProperty("Level").GetString());
                Assert.Contains("storage-inner", json.RootElement.GetProperty("Exception").GetString());
                Assert.Equal("isolated-input-request", json.RootElement.GetProperty("Properties").GetProperty("CorrelationId").GetString());
                Assert.Contains("input-fixture", ReadShared(Path.Combine(directory, "laplace-trace-test.csv")));
            }
            finally { Log.Logger = previous; }
        });
    }

    [Fact]
    public void RealFilesRetainExceptionAndCorrelationAndSinkFailuresBecomeUnready()
    {
        WithLogDirectory(directory =>
        {
            var health = new PersistentLogHealth();
            using var logger = new LoggerConfiguration().MinimumLevel.Verbose()
                .ApplyLaplaceSinks("acceptance", console: false, health: health)
                .WriteTo.File(new FailingFormatter(), Path.Combine(directory, "controlled-failure.log"))
                .CreateLogger();
            using var activity = new Activity("log-acceptance").SetIdFormat(ActivityIdFormat.W3C).Start();
            var error = new InvalidOperationException("outer failure", new IOException("inner failure"));
            logger.ForContext("CorrelationId", "log-acceptance-request")
                .Error(error, "Controlled failure for {Operation}", "persistence-acceptance");

            var csv = ReadShared(Path.Combine(directory, "laplace-acceptance.csv"));
            Assert.Contains("log-acceptance-request", csv);
            Assert.Contains("inner failure", csv);
            string jsonLine = Assert.Single(Directory.GetFiles(directory, "laplace-acceptance-*.jsonl"));
            using var json = JsonDocument.Parse(ReadShared(jsonLine));
            Assert.Contains("outer failure", json.RootElement.GetProperty("Exception").GetString());
            Assert.Contains("inner failure", json.RootElement.GetProperty("Exception").GetString());
            var properties = json.RootElement.GetProperty("Properties");
            Assert.Equal("log-acceptance-request", properties.GetProperty("CorrelationId").GetString());
            Assert.Equal(activity.TraceId.ToHexString(), properties.GetProperty("TraceId").GetString());
            Assert.True(health.Snapshot().Ready);

            // Serilog catches formatter/write failures internally. The production
            // SelfLog bridge must make that otherwise silent failure observable.
            logger.ForContext("BreakSink", true).Warning("Controlled sink failure");
            Assert.False(health.Snapshot().Ready);
            Assert.True(health.Snapshot().FailureCount > 0);
            Assert.NotNull(health.Snapshot().LastFailureUtc);
            var output = CaptureCompletion(() => LaplaceLogging.CompleteProcess(
                new SerilogLoggerFactory(logger, dispose: false), "controlled-command", 0, health));
            Assert.Equal(1, output.ExitCode);
            Assert.Contains("LOGGING_UNAVAILABLE", output.Stderr);
            Assert.Empty(output.Stdout);
            Assert.Equal(37, LaplaceLogging.CompleteProcess((IDisposable?)null, "controlled-command", 37, health));
        });
    }

    [Fact]
    public void HealthyCompletionFlushesAndClosesRealSinksWithoutChangingTheResult()
    {
        WithLogDirectory(directory =>
        {
            var health = new PersistentLogHealth();
            var logger = new LoggerConfiguration().ApplyLaplaceSinks("completion", false, health: health).CreateLogger();
            logger.Information("completion-fixture-event");
            var output = CaptureCompletion(() => LaplaceLogging.CompleteProcess(
                new SerilogLoggerFactory(logger, dispose: true), "healthy-command", 0, health));
            Assert.Equal(0, output.ExitCode);
            Assert.Empty(output.Stderr);
            Assert.Empty(output.Stdout);
            string closed = Path.Combine(directory, "closed.csv");
            File.Move(Path.Combine(directory, "laplace-completion.csv"), closed);
            Assert.Contains("completion-fixture-event", ReadShared(closed));
        });
    }

    [Fact]
    public void DisposalFailureIsHandledAndKeepsAnExistingCommandFailureCode()
    {
        var health = new PersistentLogHealth();
        health.Initialized();
        var output = CaptureCompletion(() => LaplaceLogging.CompleteProcess(new FailedDisposal(), "mcp", 23, health));
        Assert.Equal(23, output.ExitCode);
        Assert.Contains("logger-disposal-fixture", output.Stderr);
        Assert.Contains("disposal-inner-cause", output.Stderr);
        Assert.False(health.Snapshot().Ready);
        Assert.Empty(output.Stdout);
    }

    [Fact]
    public void FatalReportingFailureRetainsTheOriginalCauseAndSignalsLoggingFailure()
    {
        var health = new PersistentLogHealth();
        health.Initialized();
        var output = CaptureCompletion(() =>
        {
            LaplaceLogging.ReportProcessFailure("mcp", new IOException("original-process-fixture"),
                _ => throw new InvalidOperationException("fatal-logger-fixture"), health);
            return LaplaceLogging.CompleteProcess((IDisposable?)null, "mcp", 0, health);
        });
        Assert.Equal(1, output.ExitCode);
        Assert.Contains("original-process-fixture", output.Stderr);
        Assert.Contains("fatal-logger-fixture", output.Stderr);
        Assert.Empty(output.Stdout);
    }

    [Fact]
    public void ClosedEmergencyOutputCannotEscapeAsAnUnhandledException()
    {
        var previous = Console.Error;
        var closed = new StringWriter();
        closed.Dispose();
        var health = new PersistentLogHealth();
        health.Initialized();
        try
        {
            Console.SetError(closed);
            LaplaceLogging.ReportProcessFailure("mcp", new IOException("closed-output-fixture"), health: health);
            Assert.Equal(1, LaplaceLogging.CompleteProcess((IDisposable?)null, "mcp", 0, health));
            Assert.False(health.Snapshot().Ready);
        }
        finally { Console.SetError(previous); }
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureCompletion(Func<int> operation)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            return (operation(), stdout.ToString(), stderr.ToString());
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }

    private sealed class FailedDisposal : IDisposable
    {
        public void Dispose() => throw new IOException("logger-disposal-fixture", new IOException("disposal-inner-cause"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupRejectsEitherInaccessiblePersistentFile(bool json)
    {
        WithLogDirectory(directory =>
        {
            string blocked = json ? $"laplace-acceptance-{DateTime.Now:yyyyMMdd}.jsonl" : "laplace-acceptance.csv";
            Directory.CreateDirectory(Path.Combine(directory, blocked));
            var health = new PersistentLogHealth();
            Exception? error = Record.Exception(Configure);
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Startup accepted an inaccessible log file.");
            Assert.False(health.Snapshot().Ready);

            void Configure()
            {
                using var logger = new LoggerConfiguration()
                    .ApplyLaplaceSinks("acceptance", console: false, health: health).CreateLogger();
            }
        });
    }

    private static void WithLogDirectory(Action<string> action)
    {
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string directory = Path.GetFullPath(Path.Combine(temp, "laplace-log-acceptance-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(temp, directory, StringComparison.OrdinalIgnoreCase);
        string? previous = Environment.GetEnvironmentVariable("LAPLACE_OPS_LOG_DIR");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_OPS_LOG_DIR", directory);
            action(directory);
        }
        finally
        {
            SelfLog.Disable();
            Environment.SetEnvironmentVariable("LAPLACE_OPS_LOG_DIR", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class FailingFormatter : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            if (logEvent.Properties.ContainsKey("BreakSink"))
                throw new IOException("Controlled persistence failure.");
            output.WriteLine("healthy");
        }
    }
}
