using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class DocumentIngestPipelineTests
{
    [Fact]
    public async Task DocumentPipeline_BatchedProbe_OneDescentPerChunk()
    {
        const int docCount = 8;
        var records = Enumerable.Range(1, docCount)
            .Select(i => ContentRecord($"user document sentence number {i}."))
            .ToList();

        var reader = new ProbeTrackingReader(present: false);
        var config = DocumentIngestSupport.PipelineConfig("document/test", reader, batchSize: docCount);
        config = new IngestBatchConfig
        {
            SourceId = config.SourceId,
            BatchLabelPrefix = config.BatchLabelPrefix,
            BatchSize = config.BatchSize,
            ProbeChunkSize = docCount,
            WitnessWeight = config.WitnessWeight,
            ContainmentReader = reader,
        };

        await foreach (var _ in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new DocumentIngestHandler(), config))
        { }

        Assert.Equal(0, reader.LegacyContentDescentCalls);
        Assert.InRange(reader.FlatProbeCalls, 1,
            MaxProbeCallsFor(ExpectedExistenceRoundChunks(docCount, docCount)));
    }

    [Fact]
    public async Task DocumentPipeline_PresentReader_SkipsEntities()
    {
        var records = Enumerable.Range(1, 6)
            .Select(i => ContentRecord($"present document {i}"))
            .ToList();

        var baseline = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new DocumentIngestHandler(),
            DocumentIngestSupport.PipelineConfig("document/base", null, batchSize: 6)))
            baseline.Add(c);
        Assert.True(ContentEntityCount(baseline) > 0);

        var reader = new ProbeTrackingReader(present: true);
        var changes = new List<SubstrateChange>();
        var config = DocumentIngestSupport.PipelineConfig("document/present", reader, batchSize: 6);
        config = new IngestBatchConfig
        {
            SourceId = config.SourceId,
            BatchLabelPrefix = config.BatchLabelPrefix,
            BatchSize = config.BatchSize,
            ProbeChunkSize = 6,
            WitnessWeight = config.WitnessWeight,
            ContainmentReader = reader,
        };
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new DocumentIngestHandler(), config))
            changes.Add(c);

        Assert.True(reader.FlatProbeCalls >= 1, "root bulk IN on present documents");
        Assert.Equal(0, reader.LegacyContentDescentCalls);
        Assert.True(ContentEntityCount(changes) <= ContentEntityCount(baseline));
        // Pillar-3a: the re-witness grind is gone — documents emit ZERO distributional
        // attestations, on present trees or otherwise (sequence = trajectory geometry).
        Assert.Equal(0, AttestationCount(changes));
    }

    [Fact]
    public async Task DocumentMultiFileStream_ReadsIncrementally()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-doc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.txt"), "first document text.");
            await File.WriteAllTextAsync(Path.Combine(dir, "b.txt"), "second document text.");

            var labels = new List<string>();
            await foreach (var (label, _) in new DocumentMultiFileStream(dir).RecordsAsync())
                labels.Add(label);

            Assert.Equal(2, labels.Count);
            Assert.Contains(labels, l => l.EndsWith("a.txt"));
            Assert.Contains(labels, l => l.EndsWith("b.txt"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
