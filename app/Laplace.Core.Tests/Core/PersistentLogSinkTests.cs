using System.Diagnostics;
using System.Text.Json;
using Laplace.Ops;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Xunit;

namespace Laplace.Core.Tests;

[CollectionDefinition("Persistent logging", DisableParallelization = true)]
public sealed class PersistentLoggingCollection;

[Collection("Persistent logging")]
public sealed class PersistentLogSinkTests
{
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
        });
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
