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
    public void CsvRetentionKeepsTheActiveSqlPathAndBoundsTheRetainedFiles()
    {
        WithLogDirectory(directory => WithRetention("2048", "3", () =>
        {
            var health = new PersistentLogHealth();
            using var logger = new LoggerConfiguration().ApplyLaplaceSinks("retention", false, health: health).CreateLogger();
            for (int i = 0; i < 40; i++)
                logger.Information("Retention event {Sequence}: {Payload}", i, new string('x', 200));
            Assert.True(health.Snapshot().Ready);
            var files = Directory.GetFiles(directory, "laplace-retention*.csv");
            Assert.Equal(3, files.Length);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 2048));
            Assert.Contains("Retention event 39:", ReadShared(Path.Combine(directory, "laplace-retention.csv")));
            Assert.DoesNotContain("Retention event 0:", string.Concat(files.Select(ReadShared)));
        }));
    }

    [Fact]
    public void IndependentCsvWritersDoNotLoseOrInterleaveMultilineRecordsAcrossRollover()
    {
        WithLogDirectory(directory =>
        {
            string path = Path.Combine(directory, "laplace-concurrent.csv");
            var sinks = Enumerable.Range(0, 4).Select(_ =>
                new RetainedCsvFileSink(path, new OpsLogCsvTextFormatter("concurrent"), 2048, 1000)).ToArray();
            Parallel.For(0, sinks.Length, writer =>
            {
                using var logger = new LoggerConfiguration().WriteTo.Sink(sinks[writer]).CreateLogger();
                for (int record = 0; record < 30; record++)
                    logger.ForContext("RecordId", $"{writer}:{record}")
                        .Error(new IOException("quoted, \"cause\"\nand another line"), "Concurrent record {Writer}:{Record}", writer, record);
            });
            var ids = ReadCsvRecords(directory, "laplace-concurrent*.csv")
                .Select(fields =>
                {
                    Assert.Equal(6, fields.Length);
                    using var detail = JsonDocument.Parse(fields[5]);
                    Assert.Contains("and another line", detail.RootElement.GetProperty("Exception").GetString());
                    return detail.RootElement.GetProperty("Properties").GetProperty("RecordId").GetString();
                }).ToArray();
            Assert.Equal(120, ids.Length);
            Assert.Equal(120, ids.Distinct().Count());
        });
    }

    [Fact]
    public void OversizedEventsRemainCompleteAndAnotherRolesFilesArePreserved()
    {
        WithLogDirectory(directory => WithRetention("1024", "1", () =>
        {
            string unrelated = Path.Combine(directory, "laplace-oversized.archive-operator.csv");
            File.WriteAllText(unrelated, "operator-owned");
            var health = new PersistentLogHealth();
            using var logger = new LoggerConfiguration().ApplyLaplaceSinks("oversized", false, health: health).CreateLogger();
            logger.Information("first");
            logger.Information("Oversized {Payload}", new string('x', 4000));
            Assert.True(health.Snapshot().Ready);
            Assert.Contains(new string('x', 4000), ReadShared(Path.Combine(directory, "laplace-oversized.csv")));
            Assert.Equal("operator-owned", File.ReadAllText(unrelated));
            logger.Information("last");
            Assert.Contains("last", ReadShared(Path.Combine(directory, "laplace-oversized.csv")));
            Assert.Equal(2, Directory.GetFiles(directory, "laplace-oversized*.csv").Length);
        }));
    }

    [Fact]
    public void BlockedRolloverRetainsTheEventAndReportsUnhealthyPersistence()
    {
        if (!OperatingSystem.IsWindows()) return; // Delete sharing is a Windows file-handle contract.
        WithLogDirectory(directory => WithRetention("1024", "3", () =>
        {
            var health = new PersistentLogHealth();
            using var logger = new LoggerConfiguration().ApplyLaplaceSinks("blocked-roll", false, health: health).CreateLogger();
            logger.Information("{Payload}", new string('x', 900));
            using (var reader = File.Open(Path.Combine(directory, "laplace-blocked-roll.csv"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite))
            {
                logger.Error(new IOException("rollover-cause"), "record-during-blocked-rollover");
                Assert.False(health.Snapshot().Ready);
                Assert.Contains("record-during-blocked-rollover", ReadShared(Path.Combine(directory, "laplace-blocked-roll.csv")));
                Assert.Contains("record-during-blocked-rollover", string.Concat(Directory.GetFiles(directory, "*.jsonl").Select(ReadShared)));
            }
            logger.Information("record-after-reader-closes");
            Assert.Contains("record-after-reader-closes", ReadShared(Path.Combine(directory, "laplace-blocked-roll.csv")));
            Assert.False(health.Snapshot().Ready); // A later successful write cannot erase the failed retention.
        }));
    }

    [Fact]
    public void BlockedArchiveDeletionIsVisibleAndDoesNotSuppressTheIndependentJsonEvent()
    {
        if (!OperatingSystem.IsWindows()) return;
        WithLogDirectory(directory => WithRetention("1024", "2", () =>
        {
            var health = new PersistentLogHealth();
            using var logger = new LoggerConfiguration().ApplyLaplaceSinks("blocked-prune", false, health: health).CreateLogger();
            logger.Information("{Payload}", new string('x', 900));
            logger.Information("{Payload}", new string('y', 900));
            string archive = Assert.Single(Directory.GetFiles(directory, "laplace-blocked-prune.archive-*.csv"));
            using var reader = File.Open(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            logger.Error(new IOException("prune-cause"), "record-during-blocked-deletion");
            Assert.False(health.Snapshot().Ready);
            Assert.Contains("record-during-blocked-deletion", ReadShared(Path.Combine(directory, "laplace-blocked-prune.csv")));
            Assert.Contains("record-during-blocked-deletion", string.Concat(Directory.GetFiles(directory, "*.jsonl").Select(ReadShared)));
        }));
    }

    [Theory]
    [InlineData("0", "3")]
    [InlineData("-1", "3")]
    [InlineData("bytes", "3")]
    [InlineData("1024", "0")]
    [InlineData("1024", "2147483648")]
    public void InvalidRetentionConfigurationCannotStartAHealthyLogger(string bytes, string files)
    {
        WithLogDirectory(_ => WithRetention(bytes, files, () =>
        {
            var health = new PersistentLogHealth();
            Assert.Throws<InvalidOperationException>(() =>
                new LoggerConfiguration().ApplyLaplaceSinks("invalid-retention", false, health: health).CreateLogger());
            Assert.False(health.Snapshot().Ready);
        }));
    }

    private static IEnumerable<string[]> ReadCsvRecords(string directory, string pattern)
    {
        foreach (string file in Directory.GetFiles(directory, pattern))
        {
            using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(file);
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            while (!parser.EndOfData) yield return parser.ReadFields()!;
        }
    }

    [Fact]
    public void IncompleteActiveRecordIsPreservedAndRejectedBeforeAnyFurtherAppend()
    {
        WithLogDirectory(directory =>
        {
            string path = Path.Combine(directory, "laplace-incomplete.csv");
            File.WriteAllText(path, "incomplete,\"record");
            var health = new PersistentLogHealth();
            var failure = Assert.Throws<InvalidDataException>(() =>
                new LoggerConfiguration().ApplyLaplaceSinks("incomplete", false, health: health).CreateLogger());
            Assert.Contains(path, failure.Message);
            Assert.Equal("incomplete,\"record", File.ReadAllText(path));
            Assert.False(health.Snapshot().Ready);
        });
    }

    [Fact]
    public void InterruptedReplacementCannotBeSilentlyIgnoredOnTheNextStartup()
    {
        WithLogDirectory(directory =>
        {
            string pending = Path.Combine(directory, ".laplace-interrupted.csv.pending-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(pending, "interrupted-event-fixture");
            var health = new PersistentLogHealth();
            var output = CaptureCompletion(() =>
            {
                var logger = new LoggerConfiguration().ApplyLaplaceSinks("interrupted", false, health: health).CreateLogger();
                return LaplaceLogging.CompleteProcess(logger, "interrupted", 0, health);
            });
            Assert.Equal(1, output.ExitCode);
            Assert.Contains("CSV_INCOMPLETE_ROLLOVER", output.Stderr);
            Assert.Contains(pending, output.Stderr);
            Assert.Equal("interrupted-event-fixture", File.ReadAllText(pending));
            Assert.False(health.Snapshot().Ready);
        });
    }

    [Fact]
    public void ClockRollbackCannotCauseTheNewestArchiveToBePrunedBeforeOlderOnes()
    {
        WithLogDirectory(directory => WithRetention("1024", "2", () =>
        {
            string oldArchive = Path.Combine(directory, "laplace-clock.archive-"
                + DateTime.UtcNow.AddDays(1).Ticks.ToString("D19") + "-" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllText(oldArchive, "before-clock-rollback\n");
            var health = new PersistentLogHealth();
            using var logger = new LoggerConfiguration().ApplyLaplaceSinks("clock", false, health: health).CreateLogger();
            logger.Information("retained-before-roll {Payload}", new string('x', 700));
            logger.Information("retained-after-roll {Payload}", new string('y', 700));
            var retained = string.Concat(Directory.GetFiles(directory, "laplace-clock*.csv").Select(ReadShared));
            Assert.DoesNotContain("before-clock-rollback", retained);
            Assert.Contains("retained-before-roll", retained);
            Assert.Contains("retained-after-roll", retained);
            Assert.True(health.Snapshot().Ready);
        }));
    }

    private static void WithRetention(string bytes, string files, Action action)
    {
        string? oldBytes = Environment.GetEnvironmentVariable("LAPLACE_LOG_FILE_BYTES");
        string? oldFiles = Environment.GetEnvironmentVariable("LAPLACE_LOG_RETAINED_FILES");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_LOG_FILE_BYTES", bytes);
            Environment.SetEnvironmentVariable("LAPLACE_LOG_RETAINED_FILES", files);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_LOG_FILE_BYTES", oldBytes);
            Environment.SetEnvironmentVariable("LAPLACE_LOG_RETAINED_FILES", oldFiles);
        }
    }

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

    [Theory]
    [InlineData("operation")]
    [InlineData("reporter")]
    [InlineData("shutdown")]
    public void AnExceptionWithBrokenFormattingCannotCrashTheFailureReporter(string boundary)
    {
        var health = new PersistentLogHealth();
        health.Initialized();
        var failure = new UnrenderableException("unrenderable-fixture-cause");
        var output = CaptureCompletion(() =>
        {
            if (boundary == "shutdown")
                return LaplaceLogging.CompleteProcess(() => throw failure, "formatting-fixture", 0, health);
            LaplaceLogging.ReportProcessFailure("formatting-fixture",
                boundary == "operation" ? failure : new IOException("original-operation-fixture"),
                boundary == "reporter" ? _ => throw failure : null, health);
            return LaplaceLogging.CompleteProcess((IDisposable?)null, "formatting-fixture", 0, health);
        });
        Assert.Equal(1, output.ExitCode);
        Assert.Contains(nameof(UnrenderableException), output.Stderr);
        Assert.Contains("unrenderable-fixture-cause", output.Stderr);
        Assert.Empty(output.Stdout);
    }

    private sealed class UnrenderableException(string message) : Exception(message)
    {
        public override string ToString() => throw new IOException("exception-ToString-fixture");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeFailureNotificationRetainsTheCauseAndTerminationState(bool terminating)
    {
        WithLogDirectory(directory =>
        {
            var previous = Log.Logger;
            var health = new PersistentLogHealth();
            using var logger = new LoggerConfiguration().ApplyLaplaceSinks("runtime-failure", false, health: health).CreateLogger();
            try
            {
                Log.Logger = logger;
                var output = CaptureCompletion(() =>
                {
                    // Exercise the registered notification handler directly. This
                    // test does not create an unhandled exception/crash dialog.
                    ProcessFailureEvents.OnUnhandledException(null, new UnhandledExceptionEventArgs(
                        new IOException("runtime-event-fixture", new InvalidOperationException("runtime-inner-fixture")), terminating));
                    return 0;
                });
                Assert.Contains("runtime-inner-fixture", output.Stderr);
                Assert.Empty(output.Stdout);
                using var json = JsonDocument.Parse(ReadShared(Assert.Single(Directory.GetFiles(directory, "*.jsonl"))));
                Assert.Equal("Fatal", json.RootElement.GetProperty("Level").GetString());
                Assert.Contains("runtime-inner-fixture", json.RootElement.GetProperty("Exception").GetString());
                Assert.Equal(terminating, json.RootElement.GetProperty("Properties").GetProperty("IsTerminating").GetBoolean());
                Assert.Contains("runtime-inner-fixture", ReadShared(Path.Combine(directory, "laplace-runtime-failure.csv")));
                Assert.True(health.Snapshot().Ready);
            }
            finally { Log.Logger = previous; }
        });
    }

    [Fact]
    public void UnobservedTaskNotificationIsLoggedWithoutChangingRuntimeObservationPolicy()
    {
        WithLogDirectory(directory =>
        {
            var previous = Log.Logger;
            var health = new PersistentLogHealth();
            using var logger = new LoggerConfiguration().ApplyLaplaceSinks("task-failure", false, health: health).CreateLogger();
            try
            {
                Log.Logger = logger;
                var notification = new UnobservedTaskExceptionEventArgs(new AggregateException(new IOException("unobserved-task-fixture")));
                var output = CaptureCompletion(() => { ProcessFailureEvents.OnUnobservedTaskException(null, notification); return 0; });
                Assert.False(notification.Observed);
                Assert.Contains("unobserved-task-fixture", output.Stderr);
                Assert.Empty(output.Stdout);
                using var json = JsonDocument.Parse(ReadShared(Assert.Single(Directory.GetFiles(directory, "*.jsonl"))));
                Assert.Equal("Error", json.RootElement.GetProperty("Level").GetString());
                Assert.Contains("unobserved-task-fixture", json.RootElement.GetProperty("Exception").GetString());
                Assert.True(health.Snapshot().Ready);
            }
            finally { Log.Logger = previous; }
        });
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
