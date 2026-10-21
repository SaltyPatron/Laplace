using System.Collections.Concurrent;
using System.Diagnostics;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("GrammarPerfcache")]
public sealed class RuntimeFailureDiagnosticsTests
{
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
