using System.Collections.Concurrent;
using System.Diagnostics;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("GrammarPerfcache")]
public sealed class RuntimeFailureDiagnosticsTests
{
    [Fact]
    public void ApplyPhaseLoggerFailuresRetainTheirCausesAndCannotReplaceTheOperationFailure()
    {
        using var capture = new FailureCapture();
        var sinkFailure = new IOException("apply-log-sink-fixture", new InvalidOperationException("apply-log-inner"));
        var operationFailure = new InvalidDataException("original-apply-operation-fixture");
        var returned = Assert.Throws<InvalidDataException>((Action)(() =>
        {
            using var phase = new NpgsqlSubstrateWriter.ApplyDiagnosticPhase(new ThrowingApplyLogger(sinkFailure),
                "control-transaction-commit", null, null, null, null);
            throw operationFailure;
        }));
        Assert.Same(operationFailure, returned);
        Assert.Contains("APPLY_DIAGNOSTIC_FAILED phase=control-transaction-commit boundary=entered", capture.Text);
        Assert.Contains("boundary=exited", capture.Text);
        Assert.Contains(sinkFailure.ToString(), capture.Text);
    }

    [Fact]
    public void ApplyPhaseEnabledCheckFailureIsReportedWithoutOpeningADatabaseConnection()
    {
        using var capture = new FailureCapture();
        using var source = UnreachableDatabase();
        var writer = new NpgsqlSubstrateWriter(source, new ThrowingApplyLogger(new IOException("apply-enabled-check-fixture"), true));
        Assert.Null(writer.MeasureApplyPhase("managed-staging"));
        Assert.Contains("APPLY_DIAGNOSTIC_FAILED phase=managed-staging boundary=enabled-check", capture.Text);
        Assert.Contains("apply-enabled-check-fixture", capture.Text);
    }

    private sealed class ThrowingApplyLogger(Exception failure, bool failEnabled = false) : ILogger<NpgsqlSubstrateWriter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => failEnabled ? throw failure : true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw failure;
    }

    private static NpgsqlDataSource UnreachableDatabase() => NpgsqlDataSource.Create(
        "Host=127.0.0.1;Port=1;Username=failure-test;Database=failure-test;SSL Mode=Disable;Timeout=1;Pooling=false");

    [Fact]
    public async Task TurnFailureRetainsCauseWithoutAnOptionalWarningCallback()
    {
        using var capture = new FailureCapture();
        await using var source = UnreachableDatabase();
        await using var closer = new TurnCloser(source);
        Assert.False(await closer.CloseAsync("failure-test", ConversationContent.SessionId("failure-test", "one"), "hello", "reply"));
        Assert.True(closer.Broken);
        Assert.IsType<NpgsqlException>(closer.LastFailure);
        Assert.Contains("Conversation turn deposit failed", capture.Text, StringComparison.Ordinal);
        Assert.Contains(closer.LastFailure.ToString(), capture.Text, StringComparison.Ordinal);
        // A subsequent failure gets a fresh writer and retains its own cause.
        var first = closer.LastFailure;
        Assert.False(await closer.CloseAsync("failure-test", ConversationContent.SessionId("failure-test", "two"), "hello", "reply"));
        Assert.NotSame(first, closer.LastFailure);
    }

    [Fact]
    public async Task ArtifactFailureRemainsFailedAndRetainsItsCauseWithoutAWarningCallback()
    {
        using var capture = new FailureCapture();
        await using var source = UnreachableDatabase();
        await using var closer = new ContentArtifactCloser(source);
        var error = await Assert.ThrowsAsync<NpgsqlException>(() => closer.CloseTextAsync(
            "failure-test", "test.txt", "test.txt", "hello"u8.ToArray()));
        Assert.True(closer.Broken);
        Assert.Contains("User artifact deposit failed", capture.Text, StringComparison.Ordinal);
        // Rethrowing adds caller frames after the diagnostic is written; its
        // original type, message and underlying cause must still be retained.
        Assert.Contains(nameof(NpgsqlException), capture.Text, StringComparison.Ordinal);
        Assert.Contains(error.Message, capture.Text, StringComparison.Ordinal);
        Assert.Contains(error.GetBaseException().Message, capture.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceStartRefusesAnInvalidStopMarkerInsteadOfTreatingItAsAbsent()
    {
        var path = Path.Combine(Path.GetTempPath(), "laplace-stop-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(ManagedServiceState.OperatorStoppedAtPath(path));
            File.WriteAllText(path, "stopped");
            Assert.True(ManagedServiceState.OperatorStoppedAtPath(path));
            File.Delete(path);
            Directory.CreateDirectory(path);
            Assert.Throws<InvalidDataException>(() => ManagedServiceState.OperatorStoppedAtPath(path));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FailureCapture : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public FailureCapture() => Trace.Listeners.Add(this);
        public string Text => string.Concat(_messages);
        public override void Write(string? message) => _messages.Enqueue(message ?? "");
        public override void WriteLine(string? message) => Write(message + Environment.NewLine);
        protected override void Dispose(bool disposing)
        {
            Trace.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
