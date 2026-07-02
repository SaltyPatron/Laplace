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
        ISubstrateReader? reader, int probeChunk) =>
        new()
        {
            SourceId = TestSource,
            BatchLabelPrefix = "working-set-test",
            BatchSize = 4,
            ProbeChunkSize = probeChunk,
            ContainmentReader = reader,
            WorkingSet = true,
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
