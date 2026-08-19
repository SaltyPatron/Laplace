using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Substrate.Tests.Crud;

public sealed class TierProbeBatcherTests
{
    [Fact]
    public async Task ConcurrentSameTierRequests_PreservePositionalBitmapsAcrossSchedulerBatches()
    {
        int calls = 0;
        var probed = new System.Collections.Concurrent.ConcurrentBag<Hash128>();
        short probedTier = -1;
        var present = new HashSet<Hash128> { Id(2), Id(3) };
        var batcher = new TierProbeBatcher((ids, tier, _) =>
        {
            Interlocked.Increment(ref calls);
            foreach (var id in ids) probed.Add(id);
            probedTier = tier;
            var bitmap = new byte[BitmapBits.ByteLength(ids.Count)];
            for (int i = 0; i < ids.Count; i++)
                if (present.Contains(ids[i])) BitmapBits.Set(bitmap, i);
            return Task.FromResult(bitmap);
        });

        Task<byte[]> first = batcher.ProbeAsync([Id(1), Id(2), Id(2)], 3);
        Task<byte[]> second = batcher.ProbeAsync([Id(2), Id(3)], 3);
        byte[][] results = await Task.WhenAll(first, second);

        // There is intentionally no fake millisecond coalescing delay. If the pump gets
        // a scheduler turn between the two producers, two correct batches are lawful;
        // when both are queued together they collapse to one. Either way the union and
        // every caller's positional bitmap must be exact.
        Assert.InRange(calls, 1, 2);
        Assert.Equal(3, probedTier);
        Assert.Equal(3, probed.Distinct().Count());
        Assert.False(BitmapBits.IsSet(results[0], 0));
        Assert.True(BitmapBits.IsSet(results[0], 1));
        Assert.True(BitmapBits.IsSet(results[0], 2));
        Assert.True(BitmapBits.IsSet(results[1], 0));
        Assert.True(BitmapBits.IsSet(results[1], 1));
    }

    [Fact]
    public async Task DifferentTiers_NeverShareAProbe()
    {
        var tiers = new System.Collections.Concurrent.ConcurrentBag<short>();
        var batcher = new TierProbeBatcher((ids, tier, _) =>
        {
            tiers.Add(tier);
            return Task.FromResult(new byte[BitmapBits.ByteLength(ids.Count)]);
        });

        await Task.WhenAll(
            batcher.ProbeAsync([Id(1)], 1),
            batcher.ProbeAsync([Id(2)], 2));

        Assert.Equal(2, tiers.Count);
        Assert.Contains((short)1, tiers);
        Assert.Contains((short)2, tiers);
    }

    [Fact]
    public async Task SameTier_CoalescesUpToTheByteDerivedIdBudget()
    {
        var widths = new System.Collections.Concurrent.ConcurrentBag<int>();
        var batcher = new TierProbeBatcher((ids, _, _) =>
        {
            widths.Add(ids.Count);
            return Task.FromResult(new byte[BitmapBits.ByteLength(ids.Count)]);
        }, maxProbeIds: 2);

        await Task.WhenAll(
            batcher.ProbeAsync([Id(1)], 1),
            batcher.ProbeAsync([Id(2)], 1),
            batcher.ProbeAsync([Id(3)], 1));

        Assert.Equal(3, widths.Sum());
        Assert.All(widths, width => Assert.InRange(width, 1, 2));
    }

    [Fact]
    public async Task CancelledCaller_DoesNotCancelSharedProbe()
    {
        using var cancelled = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batcher = new TierProbeBatcher(async (ids, _, _) =>
        {
            await release.Task;
            var bitmap = new byte[BitmapBits.ByteLength(ids.Count)];
            for (int i = 0; i < ids.Count; i++) BitmapBits.Set(bitmap, i);
            return bitmap;
        });

        Task<byte[]> abandoned = batcher.ProbeAsync([Id(1)], 2, cancelled.Token);
        Task<byte[]> live = batcher.ProbeAsync([Id(2)], 2);
        await Task.Delay(10);
        cancelled.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.True(BitmapBits.IsSet(await live, 0));
    }

    private static Hash128 Id(int value) => new((ulong)value, 0);
}
