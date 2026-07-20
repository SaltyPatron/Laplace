using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class ParallelIngestPipelineTests
{
    private static readonly Hash128 Src = SubstrateCanonicalIds.Of("source", "test", "parallel-ingest");

    private sealed class ParallelIntRecordStream(int count, int workerCount) : IRecordStream<int>
    {
        public async IAsyncEnumerable<int> RecordsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var raw = Channel.CreateBounded<int>(new BoundedChannelOptions(workerCount * 8)
            {
                SingleWriter = true, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
            });
            var output = Channel.CreateBounded<int>(new BoundedChannelOptions(workerCount * 4)
            {
                SingleWriter = false, SingleReader = true, FullMode = BoundedChannelFullMode.Wait,
            });

            var feeder = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        if ((i & 1023) == 0) await Task.Yield();
                        await raw.Writer.WriteAsync(i, ct);
                    }
                }
                finally { raw.Writer.TryComplete(); }
            }, ct);

            var workers = new Task[workerCount];
            for (int w = 0; w < workerCount; w++)
            {
                workers[w] = Task.Run(async () =>
                {
                    while (await raw.Reader.WaitToReadAsync(ct))
                    {
                        while (raw.Reader.TryRead(out var i))
                            await output.Writer.WriteAsync(i, ct);
                    }
                }, ct);
            }

            var closer = Task.Run(async () =>
            {
                await Task.WhenAll(workers);
                output.Writer.TryComplete();
            }, ct);

            await foreach (var i in output.Reader.ReadAllAsync(ct))
                yield return i;

            await feeder.ConfigureAwait(false);
            await closer.ConfigureAwait(false);
        }
    }

    private static Hash128 EntityIdFor(int i) => Hash128.OfCanonical($"parallel/rec/{i}");

    private static async Task<HashSet<Hash128>> ComposeIdsAsync(int n, int workers, int batchSize)
    {
        var ids = new ConcurrentBag<Hash128>();
        var config = new IngestBatchConfig
        {
            SourceId = Src,
            BatchLabelPrefix = "parallel-unit",
            BatchSize = batchSize,
            WorkingSet = WorkingSetMode.Enabled,
        };
        var handler = new DirectComposeHandler<int>((i, b) =>
            b.AddEntity(EntityIdFor(i), EntityTier.Word, Src, Src));

        await foreach (var change in IngestBatchPipeline.RunAsync(
                           new ParallelIntRecordStream(n, workers), handler, config, CancellationToken.None))
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
        var got = await ComposeIdsAsync(n, workers, batchSize: 64);
        Assert.Equal(expected.Count, got.Count);
        Assert.True(expected.SetEquals(got));
    }

    [Fact]
    public async Task ParallelAndSerialProduceIdenticalIdSets()
    {
        const int n = 4096;
        var serial = await ComposeIdsAsync(n, workers: 1, batchSize: 64);
        var parallel = await ComposeIdsAsync(n, workers: 8, batchSize: 64);
        Assert.True(serial.SetEquals(parallel));
    }
}
