using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class IngestBatchPipelineTests
{
    [Fact]
    public async Task BatchProbeAmortization_OneDescentCallPerProbeChunk()
    {
        const int rowCount = 20;
        const int probeChunk = rowCount;
        var records = Enumerable.Range(1, rowCount)
            .Select(i => ContentRecord($"probe word {i}"))
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
        var config = DefaultConfig(reader, batchSize: rowCount, probeChunk: probeChunk);

        var changes = new List<SubstrateChange>();
        await foreach (var change in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), config))
            changes.Add(change);

        Assert.Equal(0, reader.LegacyContentDescentCalls);
        Assert.True(reader.FlatCandidateCounts.Count >= 1);
        Assert.Equal(rowCount, reader.FlatCandidateCounts[0]);
        Assert.True(reader.FlatProbeCalls >= 2, "root bulk IN then tier rounds");
        Assert.InRange(reader.FlatProbeCalls, 2,
            MaxProbeCallsFor(ExpectedExistenceRoundChunks(rowCount, probeChunk)));
        Assert.True(ContentEntityCount(changes) > 0);
    }

    [Fact]
    public async Task BatchedDescent_AmortizesRoundTrips()
    {
        const int rowCount = 100;
        const int probeChunk = 100;
        var records = Enumerable.Range(1, rowCount)
            .Select(i => ContentRecord($"batched descent {i}"))
            .ToList();

        var reader = new ProbeTrackingReader(present: false);
        var config = DefaultConfig(reader, batchSize: rowCount, probeChunk: probeChunk);

        await foreach (var _ in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), config))
        { }

        Assert.Equal(0, reader.LegacyContentDescentCalls);
        Assert.True(reader.FlatProbeCalls >= 1, "root bulk IN + tier rounds");
        Assert.InRange(reader.FlatProbeCalls, 1, MaxProbeCallsFor(1));
    }

    [Fact]
    public async Task PresentBatch_EmitsZeroEntities()
    {
        var records = Enumerable.Range(1, 12)
            .Select(i => ContentRecord($"present batch {i}"))
            .ToList();

        var baseline = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig()))
            baseline.Add(c);
        Assert.True(ContentEntityCount(baseline) > 0);

        var reader = new ProbeTrackingReader(present: true);
        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig(reader)))
            changes.Add(c);

        Assert.Equal(1, reader.FlatProbeCalls);
        Assert.Equal(0, reader.LegacyContentDescentCalls);
        Assert.Equal(0, ContentEntityCount(changes));
        Assert.Equal(records.Count, changes.Sum(x => x.Metadata.InputUnitsConsumed));
    }

    [Fact]
    public async Task PresentBatch_StillEmitsAttestations()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-present-attest-{Guid.NewGuid():N}.tsv");
        try
        {
            var lines = Enumerable.Range(1, 8)
                .Select(i => $"{i}\tRelatedTo\t/c/en/dog{i}\t/c/en/animal{i}\t{{}}")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);

            var witness = new AttestingGrammarWitness("tsv");
            var reader = new ProbeTrackingReader(present: true);

            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileViaPipelineAsync(
                path, "tsv", TestSource, witness, batchSize: 8, witnessWeight: 1.0,
                batchLabelPrefix: "present-attest", reportUnits: null, containmentReader: reader))
                changes.Add(change);

            Assert.Equal(0, ContentEntityCount(changes));
            Assert.True(AttestationCount(changes) > 0,
                "present rows must still attest via witness walk after probe-gated drain");
            Assert.Equal(lines.Length, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task NovelBatch_EmitsSameAsNoReader()
    {
        var records = Enumerable.Range(1, 12)
            .Select(i => ContentRecord($"novel batch {i}"))
            .ToList();

        var baseline = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig()))
            baseline.Add(c);

        var reader = new ProbeTrackingReader(present: false);
        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig(reader)))
            changes.Add(c);

        Assert.Equal(ContentEntityCount(baseline), ContentEntityCount(changes));
        Assert.Equal(
            baseline.Sum(x => x.Metadata.InputUnitsConsumed),
            changes.Sum(x => x.Metadata.InputUnitsConsumed));
    }

    [Fact]
    public async Task Tier01Completion_FlatProbeMarksPresentWhenTrunksAbsent()
    {
        var records = new[] { ContentRecord("dog") };
        var reader = new Tier01PresentReader();

        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig(reader)))
            changes.Add(c);

        Assert.True(reader.Tier01FlatCalls >= 2,
            "root gate plus tier rounds all flow through the flat probe surface");
        Assert.Equal(0, reader.LegacyContentDescentCalls);
        var baseline = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new ContentIngestHandler(TestSource), DefaultConfig()))
            baseline.Add(c);
        Assert.True(ContentEntityCount(changes) < ContentEntityCount(baseline));
    }

    [Fact]
    public async Task ComposeAfterProbe_DescentRunsBeforeMaterialize()
    {
        const string row = "1\tRelatedTo\t/c/en/dog\t/c/en/animal\t{}";
        byte[] utf8 = Encoding.UTF8.GetBytes(row);
        using var ast = GrammarDecomposer.Parse(utf8, "tsv");
        using var composer = new GrammarRowComposer(utf8, ast, TestSource, "tsv");

        var reader = new ProbeTrackingReader(present: true);
        byte[]? bm = await composer.ProbeDescentBitmapAsync(reader);
        Assert.NotNull(bm);
        Assert.True(reader.FlatProbeCalls > 0);

        var (ents, phys, prec, _) = composer.Materialize(1.0, bm);
        Assert.Empty(ents);
        Assert.Empty(phys);
        Assert.True(prec.Length >= 0);
    }

    [Fact]
    public async Task RecordStream_YieldsIncrementally()
    {
        var records = Enumerable.Range(1, 10)
            .Select(i => ContentRecord($"stream {i}"))
            .ToList();
        var stream = new ChunkedContentStream(records, chunkSize: 2);

        int count = 0;
        await foreach (var _ in IngestBatchPipeline.RunAsync(
            stream, new ContentIngestHandler(TestSource), DefaultConfig()))
            count++;

        Assert.Equal(10, stream.YieldCount);
        Assert.True(count >= 1);
    }

    [Fact]
    public async Task StreamAdapter_NeverAllocatesFullFile()
    {
        var lines = Enumerable.Range(1, 12).Select(i => $"incremental line {i}");
        await using var stream = await IncrementalLineFileStream.CreateAsync(lines);

        int units = 0;
        await foreach (var change in IngestBatchPipeline.RunAsync(
            stream, new ContentIngestHandler(TestSource), DefaultConfig()))
            units += (int)change.Metadata.InputUnitsConsumed;

        Assert.Equal(12, units);
        Assert.True(stream.MaxReadChunk <= 32,
            $"stream adapter must read in small chunks (max read {stream.MaxReadChunk}), not slurp the whole file");
    }

    [Fact]
    public async Task MultiFileTier_ProcessesPerFileBatches()
    {
        var files = new Dictionary<string, IReadOnlyList<ContentIngestRecord>>
        {
            ["file-a"] = [ContentRecord("alpha"), ContentRecord("beta")],
            ["file-b"] = [ContentRecord("gamma")],
            ["file-c"] = [ContentRecord("delta"), ContentRecord("epsilon"), ContentRecord("zeta")],
        };

        var changes = new List<SubstrateChange>();
        await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
            new LabeledContentMultiFileStream(files),
            label => new ContentIngestHandler(TestSource),
            label => new IngestBatchConfig
            {
                SourceId = TestSource,
                BatchLabelPrefix = $"multi/{label}",
                BatchSize = 2,
                ProbeChunkSize = 1024,
                ContainmentReader = null,
            }))
            changes.Add(change);

        Assert.Equal(6, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        Assert.Contains(changes, c => c.Metadata.SourceContentUnitName.StartsWith("multi/file-a/"));
        Assert.Contains(changes, c => c.Metadata.SourceContentUnitName.StartsWith("multi/file-b/"));
        Assert.Contains(changes, c => c.Metadata.SourceContentUnitName.StartsWith("multi/file-c/"));
    }

    [Fact]
    public async Task MultiFileTier_MaxTotalUnits_StopsAcrossFiles()
    {
        var files = new Dictionary<string, IReadOnlyList<ContentIngestRecord>>
        {
            ["file-a"] = [ContentRecord("a1"), ContentRecord("a2"), ContentRecord("a3")],
            ["file-b"] = [ContentRecord("b1"), ContentRecord("b2")],
        };

        var changes = new List<SubstrateChange>();
        await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
            new LabeledContentMultiFileStream(files),
            _ => new ContentIngestHandler(TestSource),
            label => new IngestBatchConfig
            {
                SourceId = TestSource,
                BatchLabelPrefix = $"cap/{label}",
                BatchSize = 8,
                ProbeChunkSize = 1024,
            },
            maxTotalUnits: 4))
            changes.Add(change);

        Assert.Equal(4, changes.Sum(c => c.Metadata.InputUnitsConsumed));
    }

    [Fact]
    public async Task GrammarPipelineViaAdapter_MatchesStructuredIngestPresentBitmap()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-pipeline-tsv-{Guid.NewGuid():N}.tsv");
        try
        {
            var lines = Enumerable.Range(1, 8)
                .Select(i => $"{i}\tRelatedTo\t/c/en/dog{i}\t/c/en/animal{i}\t{{}}")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);

            var witness = new NullGrammarWitness("tsv");
            var reader = new ProbeTrackingReader(present: true);

            var changes = new List<SubstrateChange>();
            await foreach (var change in StructuredGrammarIngest.IngestFileViaPipelineAsync(
                path, "tsv", TestSource, witness, batchSize: 4, witnessWeight: 1.0,
                batchLabelPrefix: "via-pipeline", reportUnits: null, containmentReader: reader))
                changes.Add(change);

            Assert.True(reader.FlatProbeCalls > 0,
                "present ingest must probe (tier descent and/or trunk shortcircuit flat)");
            Assert.Equal(0, ContentEntityCount(changes));
            Assert.Equal(lines.Length, changes.Sum(c => c.Metadata.InputUnitsConsumed));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task GrammarIngest_BatchedProbe()
    {
        string path = Path.Combine(Path.GetTempPath(), $"laplace-grammar-probe-{Guid.NewGuid():N}.tsv");
        try
        {
            const int rowCount = 12;
            var lines = Enumerable.Range(1, rowCount)
                .Select(i => $"{i}\tRelatedTo\t/c/en/legacy{i}\t/c/en/target{i}\t{{}}")
                .ToArray();
            await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);

            var witness = new NullGrammarWitness("tsv");
            var reader = new ProbeTrackingReader(present: false);

            await foreach (var _ in StructuredGrammarIngest.IngestFileAsync(
                path, "tsv", TestSource, witness, batchSize: 64, witnessWeight: 1.0,
                batchLabelPrefix: "grammar-probe", reportUnits: null,
                containmentReader: reader))
            { }

            Assert.Equal(0, reader.LegacyContentDescentCalls);
            Assert.True(reader.FlatProbeCalls < rowCount,
                "grammar ingest must batch probe within pending chunks, not one probe call per row");
            Assert.InRange(reader.FlatProbeCalls, 1,
                MaxProbeCallsFor(ExpectedExistenceRoundChunks(rowCount, 1024)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
