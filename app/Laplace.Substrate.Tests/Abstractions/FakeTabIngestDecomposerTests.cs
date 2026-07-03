using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class FakeTabIngestDecomposerTests
{
    [Fact]
    public async Task DecomposeAsync_NoReader_StagesAllRecords()
    {
        var records = Enumerable.Range(1, 5)
            .Select(i => ContentRecord($"tab-row-{i}"))
            .ToList();

        var decomposer = new FakeTabIngestDecomposer(records, TestSource, batchSize: 2, workingSet: false);
        var ctx = new FakeDecomposerContext();

        var changes = new List<SubstrateChange>();
        await foreach (var change in decomposer.DecomposeAsync(ctx, DecomposerOptions.Default))
            changes.Add(change);

        Assert.Equal(5, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        Assert.True(ContentEntityCount(changes) > 0);
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public async Task DecomposeAsync_PresentReader_StagesZeroEntities()
    {
        var records = Enumerable.Range(1, 5)
            .Select(i => ContentRecord($"tab-present-{i}"))
            .ToList();

        var reader = new ProbeTrackingReader(present: true);
        var decomposer = new FakeTabIngestDecomposer(records, TestSource, batchSize: 5, containmentReader: reader);
        var ctx = new FakeDecomposerContext();

        var changes = new List<SubstrateChange>();
        await foreach (var change in decomposer.DecomposeAsync(ctx, DecomposerOptions.Default))
            changes.Add(change);

        Assert.True(reader.FlatProbeCalls >= 1, "root bulk IN on present rows");
        Assert.Equal(0, reader.LegacyContentDescentCalls);
        Assert.Equal(0, ContentEntityCount(changes));
    }

    [Fact]
    public async Task EstimateUnitCountAsync_MatchesRecordCount()
    {
        var records = new[] { ContentRecord("a"), ContentRecord("b"), ContentRecord("c") };
        var decomposer = new FakeTabIngestDecomposer(records, TestSource);
        var count = await decomposer.EstimateUnitCountAsync(new FakeDecomposerContext());
        Assert.Equal(3, count);
    }

    private sealed class FakeDecomposerContext : IDecomposerContext
    {
        public string EcosystemPath => "";
        public ISubstrateWriter Writer => throw new NotSupportedException();
        public ISubstrateReader Reader => throw new NotSupportedException();
        public Microsoft.Extensions.Logging.ILogger Logger =>
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public string SubstrateVersion => "test";
    }
}
