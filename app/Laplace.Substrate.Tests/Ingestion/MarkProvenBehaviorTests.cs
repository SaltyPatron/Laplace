using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Ingestion.Tests;

[Collection("GrammarPerfcache")]
public sealed class MarkProvenBehaviorTests
{
    [Fact]
    public async Task DrainedContentRemainsQueuedUntilAnAcknowledgedWrite()
    {
        CodepointPerfcache.LoadDefault();
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Database=unused;Username=unused");
        int flatProbes = 0;
        var reader = new NpgsqlSubstrateReader(dataSource,
            (ids, _) =>
            {
                Interlocked.Increment(ref flatProbes);
                return Task.FromResult(new byte[BitmapBits.ByteLength(ids.Count)]);
            },
            (_, _, _, _) => Task.CompletedTask,
            (ids, _, _) => Task.FromResult(new byte[BitmapBits.ByteLength(ids.Count)]));
        const string text = "queued content must not become a persisted presence claim";
        Hash128 root = TextDecomposer.ContentRootId(Encoding.UTF8.GetBytes(text))
            ?? throw new InvalidOperationException("Test content requires a native root.");

        var queued = await Compose([ContentRecord(text), ContentRecord(text)], reader);

        Assert.Single(queued);
        Assert.Equal(2, queued[0].Metadata.InputUnitsConsumed);
        Assert.True(ContentEntityCount(queued) > 0);
        Assert.False(reader.IsProvenPresent(root));
        int beforeProbe = flatProbes;
        Assert.False(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([root]), 0));
        Assert.Equal(beforeProbe + 1, flatProbes);

        // Staging dedup remains in the working-set/native stage owner. Repeated
        // input retains its observation count without duplicate canonical E rows.
        var single = await Compose([ContentRecord(text)], reader);
        Assert.Equal(ContentEntityCount(single), ContentEntityCount(queued));
        Assert.False(reader.IsProvenPresent(root));
    }

    private static async Task<List<SubstrateChange>> Compose(
        IReadOnlyList<ContentIngestRecord> records, ISubstrateReader reader)
    {
        var changes = new List<SubstrateChange>();
        var config = new IngestBatchConfig
        {
            SourceId = TestSource,
            BatchLabelPrefix = "queued-presence-test",
            BatchSize = 4,
            ProbeChunkSize = 1,
            ContainmentReader = reader,
            WorkingSet = true,
            WorkingSetProbeInterval = 1,
        };
        await foreach (var change in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), config))
            changes.Add(change);
        return changes;
    }
}
