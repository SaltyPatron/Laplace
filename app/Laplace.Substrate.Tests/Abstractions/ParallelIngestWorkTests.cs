using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Substrate.Tests.Abstractions;

public sealed class ParallelIngestWorkTests
{
    [Fact]
    public async Task RunAsync_BoundsConcurrencyAndDrainsEveryResult()
    {
        int active = 0;
        int peak = 0;
        var results = new List<int>();

        await foreach (int result in ParallelIngestWork.RunAsync(
                           Enumerable.Range(0, 12).ToArray(),
                           maxConcurrency: 3,
                           outputCapacity: 2,
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
                               new[] { 0, 1, 2 }, 2, 1, Execute))
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
                           Work(), maxConcurrency: 4, outputCapacity: 1, Execute))
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
