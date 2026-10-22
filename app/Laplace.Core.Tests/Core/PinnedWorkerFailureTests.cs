using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests;

public sealed class PinnedWorkerFailureTests
{
    [Fact]
    public async Task PartialStartupFailureWaitsForEveryStartedWorkerBeforeReturning()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int active = 0, attempts = 0;
        var failure = new IOException("worker-start-fixture");
        var workers = Enumerable.Range(0, 3).Select(_ => new Thread(() =>
        {
            Interlocked.Increment(ref active);
            try { entered.Set(); release.Wait(); }
            finally { Interlocked.Decrement(ref active); }
        }) { IsBackground = true }).ToArray();
        var call = Task.Run(() => CpuTopology.StartAndJoinWorkers(workers, thread =>
        {
            if (Interlocked.Increment(ref attempts) == 2) throw failure;
            thread.Start();
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(call.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Same(failure, await call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, active);
        Assert.Equal(2, attempts);
        Assert.False(workers[0].IsAlive);
    }

    [Fact]
    public void StartupFailureAndAnAlreadyStartedWorkerFailureBothReachTheCaller()
    {
        int attempts = 0;
        var startup = new IOException("worker-startup-owner-fixture");
        var body = new InvalidDataException("already-running-worker-fixture");
        var failure = Assert.Throws<AggregateException>(() => CpuTopology.RunPinnedParallel(10, 3,
            _ => throw body, _ => { }, thread =>
            {
                if (Interlocked.Increment(ref attempts) == 2) throw startup;
                thread.Start();
            }));
        Assert.Contains(startup, failure.InnerExceptions);
        Assert.Contains(body, failure.InnerExceptions);
    }

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
