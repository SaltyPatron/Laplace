using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Substrate.Tests.Abstractions;

public sealed class ParallelIngestWorkTests
{
    [Fact]
    public async Task WorkerAndCancellationCallbackFailuresBothReachTheCaller()
    {
        var failure = new IOException("file parse failed");
        var cancelFailure = new IOException("peer cancellation callback failed");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        var error = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await foreach (int _ in ParallelIngestWork.RunAsync(new[] { 0, 1 }, 2, Execute)) { }
        });
        Assert.Contains(failure, error.Flatten().InnerExceptions);
        Assert.Contains(cancelFailure, error.Flatten().InnerExceptions);
        Assert.Equal(0, active);

        async IAsyncEnumerable<int> Execute(int item, [EnumeratorCancellation] CancellationToken ct)
        {
            using var registration = item == 1 ? ct.Register(() => throw cancelFailure) : default;
            if (Interlocked.Increment(ref active) == 2) entered.TrySetResult();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (item == 0) throw failure;
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                yield break;
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonmentReleasesTheBlockedAndQueuedChangesEvenWhenBothSourceClosesFail(bool fail)
    {
        var closeFailures = new[] { new IOException("queued source close failed"), new IOException("blocked source close failed") };
        var owners = Enumerable.Range(0, 3)
            .Select(i => new SourceOwner(fail && i > 0 ? closeFailures[i - 1] : null)).ToArray();
        var stages = Enumerable.Range(0, 3).Select(_ => IntentStage.New(0)).ToArray();
        var changes = Enumerable.Range(0, 3).Select(i => new SubstrateChangeBuilder(default, $"worker-{i}").Build()
            with
            {
                IntentStages = [stages[i]],
                ApplyEnvelope = SubstrateApplyEnvelope.Own(owners[i], _ => ValueTask.CompletedTask),
            }).ToArray();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (fail)
            {
                var error = await Assert.ThrowsAsync<AggregateException>(ConsumeFirstAsync);
                foreach (var failure in closeFailures) Assert.Contains(failure, error.Flatten().InnerExceptions);
            }
            else await ConsumeFirstAsync();
            Assert.All(owners, owner => Assert.Equal(1, owner.Disposals));
            Assert.All(stages, stage => Assert.True(stage.IsClosed));
        }
        finally
        {
            // Idempotent releases keep a failing regression from stranding its
            // native buffers or source handles in the test process.
            foreach (var change in changes)
                try { SubstrateApplyEnvelope.Discard(change); } catch (IOException) { }
        }

        async Task ConsumeFirstAsync()
        {
            await foreach (var change in ParallelIngestWork.RunAsync(new[] { 0 }, 1, Execute,
                               releaseAbandoned: SubstrateApplyEnvelope.Discard))
            {
                SubstrateApplyEnvelope.Discard(change);
                await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                break;
            }
        }

        async IAsyncEnumerable<SubstrateChange> Execute(int item, [EnumeratorCancellation] CancellationToken ct)
        {
            for (int i = 0; i < changes.Length; ++i)
            {
                if (i == 2) blocked.TrySetResult();
                yield return changes[i];
                await Task.Yield();
            }
        }
    }

    private sealed class SourceOwner(Exception? failure) : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose()
        {
            ++Disposals;
            if (failure is not null) throw failure;
        }
    }

    [Fact]
    public async Task ConcurrentWorkerFailuresRetainBothOriginalCauses()
    {
        var failures = new[] { new IOException("file zero failed"), new IOException("file one failed") };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        var error = await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            await foreach (int _ in ParallelIngestWork.RunAsync(new[] { 0, 1 }, 2, Execute)) { }
        });
        Assert.Equal(2, error.Flatten().InnerExceptions.Count);
        foreach (var failure in failures) Assert.Contains(failure, error.Flatten().InnerExceptions);

        async IAsyncEnumerable<int> Execute(int item, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Increment(ref active) == 2) entered.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (item >= 0) throw failures[item];
            yield break;
        }
    }

    [Fact]
    public async Task WorkerFailureDuringAbandonmentIsNotConvertedToSuccessfulCancellation()
    {
        var failure = new IOException("file cleanup failed after cancellation");
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task consume = ConsumeFirstAsync();
        try { await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { failWorker.TrySetResult(); }
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => consume));

        async Task ConsumeFirstAsync()
        {
            await foreach (int _ in ParallelIngestWork.RunAsync(new[] { 0 }, 1, Execute)) break;
        }

        async IAsyncEnumerable<int> Execute(int item, [EnumeratorCancellation] CancellationToken ct)
        {
            using var registration = ct.Register(() => cancelled.TrySetResult());
            yield return item;
            await failWorker.Task;
            throw failure;
        }
    }

    [Fact]
    public async Task ThrowingCancellationCallbackCannotSkipJoiningTheWorker()
    {
        var failure = new IOException("file worker cancellation callback failed");
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task consume = ConsumeFirstAsync();
        bool returnedBeforeWorkerStopped;
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            returnedBeforeWorkerStopped = await Task.WhenAny(consume, Task.Delay(100)) == consume;
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var error = await Assert.ThrowsAsync<AggregateException>(() => consume);
        Assert.Contains(failure, error.Flatten().InnerExceptions);
        Assert.False(returnedBeforeWorkerStopped, "The pipeline returned while its file worker still owned cleanup.");

        async Task ConsumeFirstAsync()
        {
            await foreach (int _ in ParallelIngestWork.RunAsync(new[] { 0 }, 1, Execute))
                break;
        }

        async IAsyncEnumerable<int> Execute(int item, [EnumeratorCancellation] CancellationToken ct)
        {
            using var registration = ct.Register(() => throw failure);
            try
            {
                yield return item;
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
                stopped.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task RunAsync_BoundsConcurrencyAndDrainsEveryResult()
    {
        int active = 0;
        int peak = 0;
        var results = new List<int>();

        await foreach (int result in ParallelIngestWork.RunAsync(
                           Enumerable.Range(0, 12).ToArray(),
                           maxConcurrency: 3,
                           Execute))
            results.Add(result);

        Assert.Equal(24, results.Count);
        Assert.Equal(Enumerable.Range(0, 12).SelectMany(i => new[] { i * 2, i * 2 + 1 }).Order(),
            results.Order());
        Assert.InRange(peak, 2, 3);

        async IAsyncEnumerable<int> Execute(
            int item, [EnumeratorCancellation] CancellationToken ct)
        {
            int now = Interlocked.Increment(ref active);
            int seen;
            do
            {
                seen = Volatile.Read(ref peak);
                if (seen >= now) break;
            } while (Interlocked.CompareExchange(ref peak, now, seen) != seen);

            try
            {
                await Task.Delay(15, ct);
                yield return item * 2;
                yield return item * 2 + 1;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    [Fact]
    public async Task RunAsync_PropagatesProducerFailure()
    {
        async Task DrainAsync()
        {
            await foreach (int _ in ParallelIngestWork.RunAsync(
                               new[] { 0, 1, 2 }, 2, Execute))
            {
            }
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(DrainAsync);
        Assert.Equal("work 1 failed", ex.Message);

        static async IAsyncEnumerable<int> Execute(
            int item, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (item == 1) throw new InvalidOperationException("work 1 failed");
            yield return item;
        }
    }

    [Fact]
    public async Task StreamingWork_ConsumerAbandonmentCancelsAndJoinsWorkers()
    {
        int active = 0;
        int produced = 0;

        await foreach (int _ in ParallelIngestWork.RunAsync(
                           Work(), maxConcurrency: 4, Execute))
            break;

        Assert.Equal(0, Volatile.Read(ref active));
        Assert.True(Volatile.Read(ref produced) > 0);

        static async IAsyncEnumerable<int> Work(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (int i = 0; i < 1_000; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return i;
                await Task.Yield();
            }
        }

        async IAsyncEnumerable<int> Execute(
            int item, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref active);
            try
            {
                await Task.Delay(5, ct);
                Interlocked.Increment(ref produced);
                yield return item;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }
}
