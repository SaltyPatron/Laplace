using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Documents anti-patterns from docs/ingestion-write-path-architecture.md §5 and §8.4.
/// These tests assert the generic pipeline does NOT exhibit the wrong patterns.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class IngestPipelineWrongPatternTests
{
    /// <summary>
    /// WRONG: per-row flat EntitiesExistBitmapAsync over every compose entity id when tree descent
    /// is available. RIGHT: one ContentDescentBitmapAsync per probe chunk + batched tier01 flat probe.
    /// </summary>
    [Fact]
    public async Task WrongPattern_FlatProbeOverAllEntityIds_IsNotWhatPipelineDoes()
    {
        const int rowCount = 10;
        const int probeChunk = rowCount;
        var records = Enumerable.Range(1, rowCount)
            .Select(i => ContentRecord($"wrong-pattern probe {i} with enough text to compose"))
            .ToList();

        int totalTier01 = 0;
        int totalNodes = 0;
        foreach (var r in records)
        {
            using var tree = IntentStage.BuildContentTree(r.CanonicalUtf8);
            if (tree is null) continue;
            totalNodes += tree.NodeCount;
            TierTreeDescent.BuildTier01Probe(tree, out var tier01Ids, out _);
            totalTier01 += tier01Ids.Count;
        }

        var reader = new ProbeTrackingReader(present: false);
        await foreach (var _ in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource),
            DefaultConfig(reader, batchSize: rowCount, probeChunk: probeChunk)))
        { }

        Assert.Equal(ExpectedDescentProbeChunks(rowCount, probeChunk), reader.DescentProbeCalls);
        Assert.True(reader.MaxFlatCandidates <= totalTier01,
            $"max flat candidates {reader.MaxFlatCandidates} vs batched tier01 total {totalTier01} (all-node total {totalNodes})");
    }

    /// <summary>
    /// WRONG: mid-decompose ApplyAsync (per-row DB commit). RIGHT: yield SubstrateChange batches
    /// for bulk COPY/intent-stage apply downstream — proven via FakeTabIngestDecomposer.
    /// </summary>
    [Fact]
    public async Task WrongPattern_MidDecomposeApplyAsync_IsForbidden()
    {
        var records = Enumerable.Range(1, 6).Select(i => ContentRecord($"batch apply {i}")).ToList();
        var applyCalls = 0;
        var fakeWriter = new FakeWriter(() => Interlocked.Increment(ref applyCalls));

        var decomposer = new FakeTabIngestDecomposer(records, TestSource, batchSize: 2);
        var ctx = new FakeDecomposerContext(fakeWriter);

        var changes = new List<SubstrateChange>();
        await foreach (var change in decomposer.DecomposeAsync(ctx, DecomposerOptions.Default))
            changes.Add(change);

        Assert.Equal(0, applyCalls);
        Assert.True(changes.Count >= 2, "decomposer yields batched SubstrateChange, not per-row ApplyAsync");
        Assert.All(changes, c => Assert.True(c.Metadata.InputUnitsConsumed > 0));
    }

    /// <summary>
    /// WRONG: compose-before-probe (full materialize_phys then existence check). RIGHT: probe gates
    /// materialize — present trunk skips phys entirely.
    /// </summary>
    [Fact]
    public async Task WrongPattern_ComposeBeforeProbe_SkippedWhenAllPresent()
    {
        var records = new[] { ContentRecord("compose before probe should not happen") };
        var reader = new ProbeTrackingReader(present: true);

        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig(reader)))
            changes.Add(c);

        Assert.Equal(1, reader.DescentProbeCalls);
        Assert.Equal(0, ContentEntityCount(changes));
    }

    private sealed class FakeWriter(Action onApply) : ISubstrateWriter
    {
        public Task<ApplyResult> ApplyAsync(SubstrateChange change, CancellationToken ct = default)
        {
            onApply();
            return Task.FromResult(new ApplyResult(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, false));
        }
        public Task<ApplyResult> ApplyManyAsync(IReadOnlyList<SubstrateChange> changes, CancellationToken ct = default)
        {
            onApply();
            return Task.FromResult(new ApplyResult(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, false));
        }
    }

    private sealed class FakeDecomposerContext(ISubstrateWriter writer) : IDecomposerContext
    {
        public string EcosystemPath => "";
        public ISubstrateWriter Writer => writer;
        public ISubstrateReader Reader => throw new NotSupportedException();
        public Microsoft.Extensions.Logging.ILogger Logger =>
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public string SubstrateVersion => "test";
    }
}
