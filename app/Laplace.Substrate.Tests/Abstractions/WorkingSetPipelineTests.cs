using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Rule #8 working-set mode: one builder spans the stream, exactly one
/// SubstrateChange is emitted, each distinct id is probed at most once per
/// working set, and stage witness-dedup collapses repeated content to one
/// staged row regardless of how many records carried it.
/// </summary>
[Collection("GrammarPerfcache")]
public sealed class WorkingSetPipelineTests
{
    private static IngestBatchConfig WorkingSetConfig(
        ISubstrateReader? reader, int probeChunk, int? recordCap = null) =>
        new()
        {
            SourceId = TestSource,
            BatchLabelPrefix = "working-set-test",
            BatchSize = 4,
            ProbeChunkSize = probeChunk,
            ContainmentReader = reader,
            WorkingSet = true,
            WorkingSetProbeInterval = probeChunk,
            WorkingSetRecordCap = recordCap,
        };

    private static async Task<List<SubstrateChange>> RunAsync(
        IReadOnlyList<ContentIngestRecord> records, IngestBatchConfig config)
    {
        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), config))
            changes.Add(c);
        return changes;
    }

    [Fact]
    public async Task WorkingSet_YieldsExactlyOneChange_BatchModeYieldsMany()
    {
        var records = Enumerable.Range(1, 20)
            .Select(i => ContentRecord($"working set distinct {i}"))
            .ToList();

        var batchChanges = await RunAsync(records, DefaultConfig(
            new ProbeTrackingReader(present: false), batchSize: 4, probeChunk: 8));
        Assert.True(batchChanges.Count > 1, "batch mode must yield multiple changes at BatchSize 4");

        var wsChanges = await RunAsync(records, WorkingSetConfig(
            new ProbeTrackingReader(present: false), probeChunk: 8));

        Assert.Single(wsChanges);
        Assert.Equal(20, wsChanges[0].Metadata.InputUnitsConsumed);

        // Cross-batch stage dedup is the point of working-set mode: the 20
        // texts share words ("working", "set", ...) that batch mode
        // re-stages once per builder (its per-stage witness set resets every
        // 4 records) and working-set mode stages exactly once. A single
        // batch-mode builder spanning all rows has the same dedup scope and
        // must match exactly; the multi-batch run must be strictly larger.
        var singleBuilderBaseline = await RunAsync(records, DefaultConfig(
            new ProbeTrackingReader(present: false), batchSize: records.Count, probeChunk: 8));
        Assert.Equal(ContentEntityCount(singleBuilderBaseline), ContentEntityCount(wsChanges));
        Assert.True(ContentEntityCount(batchChanges) > ContentEntityCount(wsChanges),
            "multi-batch mode re-stages shared constituents per builder; working-set mode must not");
    }

    [Fact]
    public async Task WorkingSet_RepeatedAbsentContent_ProbesEachDistinctIdOnce()
    {
        var distinct = Enumerable.Range(1, 5)
            .Select(i => $"repeat probe {i}")
            .ToList();

        // 4 interleaved copies of the same 5 texts; probeChunk 5 => the same
        // distinct id set re-enters the gate + descent on every interval.
        var repeated = Enumerable.Range(0, 4)
            .SelectMany(_ => distinct.Select(t => ContentRecord(t)))
            .ToList();

        var repeatedReader = new ProbeTrackingReader(present: false);
        var wsRepeated = await RunAsync(repeated, WorkingSetConfig(repeatedReader, probeChunk: 5));

        var singleReader = new ProbeTrackingReader(present: false);
        var wsSingle = await RunAsync(
            distinct.Select(t => ContentRecord(t)).ToList(),
            WorkingSetConfig(singleReader, probeChunk: 5));

        // Every probe after the first interval is redundant by construction;
        // the working-set caches must reduce the 4x stream to the same probe
        // row volume as ingesting the distinct set once.
        Assert.Equal(singleReader.TotalFlatCandidates, repeatedReader.TotalFlatCandidates);

        // And witness-dedup must collapse staged payloads to the single-copy
        // volume while all 20 observations still count as consumed units.
        Assert.Equal(ContentEntityCount(wsSingle), ContentEntityCount(wsRepeated));
        Assert.Equal(20, wsRepeated.Sum(c => c.Metadata.InputUnitsConsumed));
        Assert.Equal(5, wsSingle.Sum(c => c.Metadata.InputUnitsConsumed));
    }

    [Fact]
    public async Task WorkingSet_ProvenPresentIds_NeverReachTheDatabase()
    {
        var records = Enumerable.Range(1, 10)
            .Select(i => ContentRecord($"proven present {i}"))
            .ToList();

        var reader = new ProvenAllReader();
        var changes = await RunAsync(records, WorkingSetConfig(reader, probeChunk: 4));

        Assert.Equal(0, reader.FlatProbeCalls);
        Assert.Equal(0, ContentEntityCount(changes));
        Assert.Equal(10, changes.Sum(c => c.Metadata.InputUnitsConsumed));
    }

    [Fact]
    public async Task WorkingSet_RecordCapFlushesBeforeLargerProbeInterval()
    {
        var records = Enumerable.Range(1, 10)
            .Select(i => ContentRecord($"record cap {i}"))
            .ToList();
        var config = WorkingSetConfig(
            new ProbeTrackingReader(present: false), probeChunk: 100, recordCap: 3);

        var changes = await RunAsync(records, config);

        Assert.Equal(4, changes.Count);
        Assert.Equal(10, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        Assert.All(changes.Take(3), change =>
            Assert.Equal(3, change.Metadata.InputUnitsConsumed));
    }

    [Fact]
    public void WorkingSetConcurrency_RecomputesRecordAndProbeCapsFromSharedEnvelope()
    {
        var profile = IngestSourceProfile.UdSentence;
        var config = new IngestBatchConfig
        {
            SourceId = TestSource,
            BatchLabelPrefix = "working-set-concurrency-test",
            BatchSize = 1024,
            WorkingSet = true,
            WorkingSetProbeInterval = 3276,
            WorkingSetRecordCap = 24576,
            WorkingSetProfile = profile,
            EntityCapacity = 1024 * 40,
            PhysicalityCapacity = 1024 * 32,
            AttestationCapacity = 1024 * 8,
        };

        var shared = config.WithWorkingSetConcurrency(10);
        long perSetEnvelope = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(10);
        int expected = IngestSizing.ResolveFlushEnvelopeRecordCap(profile, perSetEnvelope);

        Assert.Equal(10, shared.ConcurrentWorkingSets);
        Assert.Equal(expected, shared.EffectiveWorkingSetRecordCap);
        Assert.True(shared.EffectiveWorkingSetProbeInterval <= expected);
        Assert.Equal(24576, shared.WorkingSetRecordCap);
        Assert.Equal(24576, config.WorkingSetRecordCap);

        int residentRecords = Math.Min(config.BatchSize, expected);
        var capacities = shared.ResolveBuilderCapacities();
        Assert.Equal(residentRecords * 40, capacities.Entities);
        Assert.Equal(residentRecords * 32, capacities.Physicalities);
        Assert.Equal(residentRecords * 8, capacities.Attestations);

        int originalResidentRecords = Math.Min(
            config.BatchSize, config.EffectiveWorkingSetRecordCap);
        var originalCapacities = config.ResolveBuilderCapacities();
        Assert.Equal(originalResidentRecords * 40, originalCapacities.Entities);
        Assert.Equal(originalResidentRecords * 32, originalCapacities.Physicalities);
        Assert.Equal(originalResidentRecords * 8, originalCapacities.Attestations);
    }

    [Fact]
    public void BuilderCapacityScaling_PreservesZeroAndRoundsNonzeroCapacityUp()
    {
        var config = new IngestBatchConfig
        {
            SourceId = TestSource,
            BatchLabelPrefix = "builder-capacity-scaling-test",
            BatchSize = 2048,
            WorkingSet = true,
            WorkingSetRecordCap = 16,
            EntityCapacity = 1,
            PhysicalityCapacity = 0,
            AttestationCapacity = 2048 * 8,
        };

        var capacities = config.ResolveBuilderCapacities();

        Assert.Equal(1, capacities.Entities);
        Assert.Equal(0, capacities.Physicalities);
        Assert.Equal(16 * 8, capacities.Attestations);
    }

    [Fact]
    public void ComposeWorkers_AreOneProcessBudget_NotPerWorkingSet()
    {
        var config = WorkingSetConfig(reader: null, probeChunk: 64)
            .WithWorkingSetConcurrency(4);
        var parallelHandler = new ContentIngestHandler(TestSource);

        int actual = IngestDescentFlush.ResolveComposeWorkers(
            10_000, parallelHandler, config);
        int expected = Math.Max(1, IngestTopology.Current.ComposeWorkers / 4);

        Assert.Equal(expected, actual);
        Assert.True(actual * config.ConcurrentWorkingSets <=
                    Math.Max(config.ConcurrentWorkingSets, IngestTopology.Current.ComposeWorkers));
    }

    [Fact]
    public void ComposeWorkers_ReturnToTheLastActiveFile()
    {
        int activeFiles = 8;
        var config = WorkingSetConfig(reader: null, probeChunk: 64)
            .WithActiveWorkingSetConcurrency(8, () => Volatile.Read(ref activeFiles));
        var handler = new ContentIngestHandler(TestSource);

        int shared = IngestDescentFlush.ResolveComposeWorkers(10_000, handler, config);
        Volatile.Write(ref activeFiles, 1);
        int tail = IngestDescentFlush.ResolveComposeWorkers(10_000, handler, config);

        Assert.Equal(Math.Max(1, IngestTopology.Current.ComposeWorkers / 8), shared);
        Assert.Equal(Math.Max(1, IngestTopology.Current.ComposeWorkers), tail);
    }

    [Fact]
    public void WorkingSetEnvelope_ReturnsToTheLastActiveFile()
    {
        int activeFiles = 8;
        var config = WorkingSetConfig(reader: null, probeChunk: 64)
            .WithActiveWorkingSetConcurrency(8, () => Volatile.Read(ref activeFiles));

        int shared = config.EffectiveWorkingSetRecordCap;
        Volatile.Write(ref activeFiles, 1);
        int tail = config.EffectiveWorkingSetRecordCap;

        Assert.True(tail > shared);
        Assert.Equal(
            IngestSizing.ResolveFlushEnvelopeRecordCap(
                config.WorkingSetProfile ?? IngestSourceProfile.Default,
                IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(1)),
            tail);
    }

    [Fact]
    public void DirectCompose_DoesNotParallelizeWrapperAllocation()
    {
        var handler = new DirectComposeHandler<int>((_, _) => { });
        var config = WorkingSetConfig(reader: null, probeChunk: 64);

        Assert.Equal(1, IngestDescentFlush.ResolveComposeWorkers(10_000, handler, config));
    }

    [Fact]
    public void PipelineBuilders_DefaultToBulkContentPresenceProbe()
    {
        var reader = new ProbeTrackingReader(present: true);
        var config = IngestPipelineDefaults.Compose(
            TestSource, "deferred-default", DecomposerOptions.Default, reader);

        Assert.True(config.EnableDeferredContentOnBuilder);
        Assert.NotNull(config.NewBuilder(0).DeferredContent);
    }

    [Fact]
    public void VendorDefaultBatch_DoesNotOverrideMachineProfileSizing()
    {
        var profile = IngestSourceProfile.Default;
        var config = IngestPipelineDefaults.Compose(
            TestSource, "machine-profile", DecomposerOptions.Default,
            reader: null, profile: profile);

        Assert.Equal(IngestSizing.ResolveForSource(profile).RecordBatchSize, config.BatchSize);
    }

    [Fact]
    public void StructuredGrammar_RespectsExplicitOperatorBatch()
    {
        var options = DecomposerOptions.Default with { BatchSize = 777 };
        var config = IngestPipelineDefaults.StructuredGrammar(
            TestSource, "grammar-batch", options, reader: null);

        Assert.Equal(777, config.BatchSize);
    }

    /// <summary>
    /// Simulates a fully warmed process-lifetime proven cache: every id is
    /// already positively confirmed present, so neither the gate nor the
    /// descent may issue a single DB probe.
    /// </summary>
    private sealed class ProvenAllReader : ISubstrateReader
    {
        public int FlatProbeCalls;

        public Task<bool> HasSourceEverCompletedAsync(int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> HasSourceCompletedAsync(Hash128 sourceId, int layerOrder, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<long> CountEntitiesByTypeAsync(Hash128 typeId, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<byte[]> EntitiesExistBitmapAsync(IReadOnlyList<Hash128> candidates, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FlatProbeCalls);
            var bm = new byte[(candidates.Count + 7) / 8];
            Array.Fill(bm, (byte)0xFF);
            return Task.FromResult(bm);
        }

        public bool IsProvenPresent(Hash128 id) => true;

        public void MarkProven(IReadOnlyList<Hash128> ids) { }
    }
}
