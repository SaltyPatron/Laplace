using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Laplace.Decomposers.Abstractions;
using Laplace.Ops;
using Microsoft.Extensions.Logging;
using Npgsql;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

[CollectionDefinition("Queued witness diagnostics", DisableParallelization = true)]
public sealed class TurnWitnessDiagnosticsCollection;

[Collection("Queued witness diagnostics")]
public sealed class TurnWitnessDiagnosticsTests
{
    [Fact]
    public async Task QueuedFailuresRetainSeparateRequestContextsAndReturnTheDatabaseCause()
        => await WithWitness(
            "Host=127.0.0.1;Port=1;Username=turn_test;Database=turn_test;Timeout=1;SSL Mode=Disable;Pooling=false",
            async (witness, events, directory) =>
            {
                foreach (string correlation in new[] { "queued-request-one", "queued-request-two" })
                {
                    Task pending;
                    string parentTrace;
                    using (var request = new Activity(correlation).SetIdFormat(ActivityIdFormat.W3C).Start())
                    using (LogContext.PushProperty("CorrelationId", correlation))
                    {
                        parentTrace = request.TraceId.ToHexString();
                        pending = Record(witness, correlation);
                    }
                    var failure = await Assert.ThrowsAsync<SubstrateUnavailableException>(() =>
                        pending.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.IsType<NpgsqlException>(failure.InnerException);
                    var terminal = Assert.Single(events.Rows, e => e.MessageTemplate.Text == "turn-witness could not commit turn"
                        && Correlation(e) == correlation);
                    Assert.Equal(LogEventLevel.Error, terminal.Level);
                    Assert.IsType<NpgsqlException>(terminal.Exception);
                    Assert.Equal(parentTrace, Assert.IsType<ScalarValue>(terminal.Properties["TraceId"]).Value);
                }
                Assert.Equal(2, events.Rows.Count(e => e.MessageTemplate.Text == "turn-witness could not commit turn"));
                await using var file = new FileStream(Path.Combine(directory, "laplace-turn-test.csv"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(file);
                string csv = await reader.ReadToEndAsync();
                Assert.Contains("queued-request-one", csv);
                Assert.Contains("queued-request-two", csv);
                Assert.Contains(nameof(NpgsqlException), csv);
                Assert.DoesNotContain("diagnostic fixture prompt", csv);
                Assert.DoesNotContain("diagnostic fixture reply", csv);
            });

    [Fact]
    public async Task ShutdownReleasesAndLogsAnAbandonedQueuedTurnWithItsOriginalContext()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync();
            await WithWitness($"Host=127.0.0.1;Port={port};Username=turn_test;Database=turn_test;Timeout=30;SSL Mode=Disable;Pooling=false",
                async (witness, events, _) =>
                {
                    Task active;
                    using (LogContext.PushProperty("CorrelationId", "shutdown-active"))
                        active = Record(witness, "shutdown-active");
                    // A real accepted connection holds the first item inside Npgsql's
                    // handshake; the second item is still queued when shutdown starts.
                    using var socket = await accepted.WaitAsync(TimeSpan.FromSeconds(10));
                    Task queued;
                    using (LogContext.PushProperty("CorrelationId", "shutdown-pending"))
                        queued = Record(witness, "shutdown-pending");
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    Task stopping = witness.StopAsync(stop.Token);
                    var pendingFailure = await Assert.ThrowsAsync<SubstrateUnavailableException>(() =>
                        queued.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.IsType<InvalidOperationException>(pendingFailure.InnerException);
                    socket.Dispose(); // release the real handshake owner after the queued receipt settled
                    await stopping.WaitAsync(TimeSpan.FromSeconds(10));
                    await Assert.ThrowsAsync<SubstrateUnavailableException>(() => active);
                    Assert.Single(events.Rows, e => e.MessageTemplate.Text == "turn-witness abandoned pending turn during shutdown"
                        && Correlation(e) == "shutdown-pending");
                    Assert.Single(events.Rows, e => e.MessageTemplate.Text == "turn-witness stopped while awaiting turn commit"
                        && Correlation(e) == "shutdown-active");
                });
        }
        finally { listener.Stop(); }
    }

    private static Task Record(TurnWitness witness, string correlation) => witness.RecordTurnAsync("queue-test", null,
        ConversationContent.SessionId("queue-test", correlation), "diagnostic fixture prompt", "diagnostic fixture reply", CancellationToken.None);

    private static string? Correlation(LogEvent value) => value.Properties.TryGetValue("CorrelationId", out var property)
        && property is ScalarValue { Value: string text } ? text : null;

    private static async Task WithWitness(string connectionString, Func<TurnWitness, Capture, string, Task> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "laplace-turn-diagnostics-" + Guid.NewGuid().ToString("N"));
        string? oldDirectory = Environment.GetEnvironmentVariable("LAPLACE_OPS_LOG_DIR");
        var previous = Log.Logger;
        Environment.SetEnvironmentVariable("LAPLACE_OPS_LOG_DIR", directory);
        var events = new Capture();
        try
        {
            using var logger = new LoggerConfiguration().ApplyLaplaceSinks("turn-test", console: false,
                health: new PersistentLogHealth()).WriteTo.Sink(events).CreateLogger();
            Log.Logger = logger;
            using var factory = new SerilogLoggerFactory(logger, dispose: false);
            await using var substrate = new SubstrateClient(connectionString);
            using var witness = new TurnWitness(substrate, factory.CreateLogger<TurnWitness>());
            await witness.StartAsync(CancellationToken.None);
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!witness.IsAvailable && witness.ExecuteTask is not { IsCompleted: true })
                    await Task.Delay(10, startup.Token);
                Assert.True(witness.IsAvailable, string.Join("\n", events.Rows.Select(e => e.RenderMessage() + " " + e.Exception)));
                await check(witness, events, directory);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await witness.StopAsync(shutdown.Token);
            }
        }
        finally
        {
            Log.Logger = previous;
            Environment.SetEnvironmentVariable("LAPLACE_OPS_LOG_DIR", oldDirectory);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class Capture : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Rows { get; } = new();
        public void Emit(LogEvent logEvent) => Rows.Enqueue(logEvent);
    }
}
