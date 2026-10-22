using System.Diagnostics;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class FideWorkerFailureTests
{
    [Fact]
    public void FailureCleanupRetainsEveryDistinctWorkerCauseWithoutReplacingTheInitiatingFailure()
    {
        var primary = new InvalidDataException("fide-parser-primary-fixture");
        var first = new IOException("fide-worker-first-fixture", new IOException("fide-worker-inner-fixture"));
        var second = new InvalidOperationException("fide-worker-second-fixture");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var capture = new FailureCapture();
        var returned = Assert.Throws<InvalidDataException>((Action)(() =>
        {
            try { throw primary; }
            catch
            {
                FideRatingList.DrainFailedWorkers([Task.FromException(first), Task.FromException(second),
                    Task.FromException(first), Task.FromCanceled(canceled.Token)]);
                throw;
            }
        }));
        Assert.Same(primary, returned);
        var aggregate = Assert.Single(capture.Failures);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains(first, aggregate.InnerExceptions);
        Assert.Contains(second, aggregate.InnerExceptions);
    }

    [Fact]
    public void RequestedWorkerCancellationDoesNotProduceASpuriousSecondaryFailure()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var capture = new FailureCapture();
        FideRatingList.DrainFailedWorkers([Task.FromCanceled(canceled.Token), Task.CompletedTask]);
        Assert.Empty(capture.Failures);
    }

    private sealed class FailureCapture : TraceListener
    {
        public List<AggregateException> Failures { get; } = [];
        public FailureCapture() => Trace.Listeners.Add(this);
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
        public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id,
            string? format, params object?[]? args)
        {
            if (format?.StartsWith("FIDE_WORKER_DRAIN_FAILED", StringComparison.Ordinal) == true)
                Failures.AddRange((args ?? []).OfType<AggregateException>());
        }
        protected override void Dispose(bool disposing) { Trace.Listeners.Remove(this); base.Dispose(disposing); }
    }
}
