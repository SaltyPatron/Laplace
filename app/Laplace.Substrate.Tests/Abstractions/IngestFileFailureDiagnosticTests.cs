using System.Collections.Concurrent;
using System.Diagnostics;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class IngestFileFailureDiagnosticTests
{
    [Fact]
    public void IsolatedFileFailureRetainsTheEntireCauseWhileItsReceiptRemainsAFileFailure()
    {
        using var listener = new CapturedTrace();
        Trace.Listeners.Add(listener);
        string label = "isolated-file-" + Guid.NewGuid().ToString("N");
        var cause = new IOException("storage-inner-fixture");
        var failure = new InvalidDataException("transcript-outer-fixture", cause);
        try
        {
            var receipt = IngestBatchPipeline.BuildFileFailure(Hash128.Blake3("source-fixture"u8), label, failure);
            Assert.False(receipt.CountsAsUnit);
            Assert.StartsWith(IngestBatchPipeline.FileFailedUnitPrefix, receipt.Metadata.SourceContentUnitName);
            var diagnostic = Assert.Single(listener.Messages, value => value.Contains(label, StringComparison.Ordinal));
            Assert.Contains("INGEST_FILE_FAILED", diagnostic);
            Assert.Contains("transcript-outer-fixture", diagnostic);
            Assert.Contains("storage-inner-fixture", diagnostic);
            Assert.Contains("System.IO.IOException", diagnostic);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    private sealed class CapturedTrace : TraceListener
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public override void Write(string? message) { if (message is not null) Messages.Enqueue(message); }
        public override void WriteLine(string? message) => Write(message);
        public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id,
            string? format, params object?[]? arguments) =>
            Write(arguments is null || arguments.Length == 0 ? format : string.Format(format!, arguments));
    }
}
