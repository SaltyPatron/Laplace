using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests;

public sealed class PinnedWorkerFailureTests
{
    [Fact]
    public void DistinctWorkerFailuresAllReachTheCallingFailureBoundary()
    {
        var failure = Assert.Throws<AggregateException>(() => CpuTopology.RunPinnedParallel(10, 3, _ => { },
            index => throw new IOException($"pin-setup-{index}")));
        Assert.Equal(new[] { "pin-setup-0", "pin-setup-1", "pin-setup-2" },
            failure.InnerExceptions.Select(error => error.Message));
    }

    [Fact]
    public void WorkerInitializationFailureReturnsToTheCallerWithItsCause()
    {
        var failure = new IOException("pin-worker-setup-fixture", new InvalidOperationException("pin-setup-inner"));
        int initialized = 0;
        var returned = Assert.Throws<IOException>(() => CpuTopology.RunPinnedParallel(10, 3, _ => { }, _ =>
        {
            Interlocked.Increment(ref initialized);
            throw failure;
        }));
        Assert.Same(failure, returned);
        Assert.Equal(3, initialized);
        Assert.Contains(nameof(WorkerInitializationFailureReturnsToTheCallerWithItsCause), returned.StackTrace);
    }

    [Fact]
    public void WorkerBodyFailureReturnsOnlyAfterSiblingWorkersHaveExited()
    {
        int active = 0;
        var failure = new InvalidDataException("pinned-body-fixture");
        var returned = Assert.Throws<InvalidDataException>(() => CpuTopology.RunPinnedParallel(8, 3, _ =>
        {
            Interlocked.Increment(ref active);
            try { throw failure; }
            finally { Interlocked.Decrement(ref active); }
        }, _ => { }));
        Assert.Same(failure, returned);
        Assert.Equal(0, active);
        Assert.Contains(nameof(WorkerBodyFailureReturnsOnlyAfterSiblingWorkersHaveExited), returned.StackTrace);
    }
}
