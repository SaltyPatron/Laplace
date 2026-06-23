using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class PCoreParallelComposeTests
{
    private static readonly Hash128 Src = Hash128.OfCanonical("substrate/source/test/pcore-compose");

    private static async IAsyncEnumerable<int> Range(int n)
    {
        for (int i = 0; i < n; i++)
        {
            if ((i & 1023) == 0) await Task.Yield();
            yield return i;
        }
    }

    // Each record i contributes a deterministic content-addressed entity id; the union of emitted
    // entity ids must be EXACTLY {id(i) : i in [0,n)} regardless of worker count — proving the
    // parallel fan-out loses no record and duplicates none (cross-builder repeats fold by id).
    private static Hash128 EntityIdFor(int i) => Hash128.OfCanonical($"pcore/rec/{i}");

    private static async Task<HashSet<Hash128>> ComposeIdsAsync(int n, int workers)
    {
        var ids = new ConcurrentBag<Hash128>();
        var changes = PCoreParallelCompose.RunAsync(
            Range(n),
            workers,
            recordsPerChange: 64,
            () => new SubstrateChangeBuilder(Src, "pcore-unit"),
            (b, i) => b.AddEntity(EntityIdFor(i), EntityTier.Vocabulary, Src, Src),
            CancellationToken.None);

        await foreach (var change in changes)
            foreach (var e in change.Entities)
                ids.Add(e.Id);

        return ids.ToHashSet();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task EmitsEveryRecordExactlyOnce_AcrossWorkerCounts(int workers)
    {
        const int n = 5000;
        var expected = Enumerable.Range(0, n).Select(EntityIdFor).ToHashSet();
        var got = await ComposeIdsAsync(n, workers);
        Assert.Equal(expected.Count, got.Count);
        Assert.True(expected.SetEquals(got), "parallel compose id-set must equal the full record set");
    }

    [Fact]
    public async Task ParallelAndSerialProduceIdenticalIdSets()
    {
        const int n = 4096;
        var serial = await ComposeIdsAsync(n, workers: 1);
        var parallel = await ComposeIdsAsync(n, workers: 8);
        Assert.True(serial.SetEquals(parallel),
            "8-worker compose must yield the same substrate id-set as the serial compose");
    }

    [Fact]
    public async Task PropagatesComposeException()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var changes = PCoreParallelCompose.RunAsync(
                Range(1000),
                workerCount: 4,
                recordsPerChange: 16,
                () => new SubstrateChangeBuilder(Src, "boom-unit"),
                (b, i) => { if (i == 500) throw new InvalidOperationException("boom"); },
                CancellationToken.None);
            await foreach (var _ in changes) { }
        });
        Assert.Equal("boom", ex.Message);
    }
}
