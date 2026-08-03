using System.IO;
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
            new ListContentStream(records), new DocumentIngestHandler(layerOrder: 2), config))
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
            new ListContentStream(records), new DocumentIngestHandler(layerOrder: 2),
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
            new ListContentStream(records), new DocumentIngestHandler(layerOrder: 2), config))
            changes.Add(c);

        Assert.True(reader.FlatProbeCalls >= 1, "root bulk IN on present documents");
        Assert.Equal(0, reader.LegacyContentDescentCalls);
        Assert.True(ContentEntityCount(changes) <= ContentEntityCount(baseline));
        // Pillar-3a: the re-witness grind is gone — documents emit ZERO distributional
        // attestations, on present trees or otherwise (sequence = trajectory geometry).
        // Present content WITHOUT a per-file completion marker still deposits the marker
        // (Pillar 0: the file's trunk-grain witness) — that is provenance, not re-witness.
        Assert.Equal(0, NonMarkerAttestationCount(changes));
        Assert.Equal(records.Count, MarkerAttestationCount(changes));
    }

    [Fact]
    public async Task DocumentPipeline_MarkerComplete_TrueSkipsBeforeCompose()
    {
        var records = Enumerable.Range(1, 5)
            .Select(i => ContentRecord($"completed document {i}"))
            .ToList();

        // Root entities present AND per-file completion markers present: the existence
        // gate must skip every file before compose — zero rows, zero testimony, no merge.
        var reader = new ProbeTrackingReader(present: true) { SourceCompleted = (_, _) => true };
        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new DocumentIngestHandler(layerOrder: 2),
            DefaultDocumentConfig(reader)))
            changes.Add(c);

        Assert.Equal(0, ContentEntityCount(changes));
        Assert.Equal(0, AttestationCount(changes));
    }

    [Fact]
    public async Task DocumentPipeline_ForceReObserve_BypassesMarkerSkip()
    {
        var records = Enumerable.Range(1, 3)
            .Select(i => ContentRecord($"forced document {i}"))
            .ToList();

        var reader = new ProbeTrackingReader(present: true) { SourceCompleted = (_, _) => true };
        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records),
            new DocumentIngestHandler(layerOrder: 2) { IgnoreCompletedFiles = true },
            DefaultDocumentConfig(reader)))
            changes.Add(c);

        // --force re-observes: files compose (content no-ops under the present bitmap)
        // and re-deposit their markers.
        Assert.Equal(records.Count, MarkerAttestationCount(changes));
    }

    // GH #596: invalid UTF-8 must not throw out of ResolveRoot / OpenAsync — one bad
    // file in a directory ingest skips; the run continues.
    [Fact]
    public void ResolveRoot_InvalidUtf8_ReturnsNull()
    {
        Assert.Null(ContentTierSpine.ResolveRoot(new byte[] { 0xFF, 0xFE }));
    }

    [Fact]
    public async Task DocumentFileExtract_InvalidUtf8_YieldsNoRecord()
    {
        string dir = Path.Combine(Path.GetTempPath(), "laplace596_" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "bad.txt");
            await File.WriteAllBytesAsync(file, new byte[] { 0xFF, 0xFE, (byte)'a' });
            var records = new List<ContentIngestRecord>();
            await foreach (var r in DocumentFileExtract.OpenAsync(file, "bad.txt", default))
                records.Add(r);
            Assert.Empty(records);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DocumentPipeline_PerFileRecord_DepositsMarkerAndMetadata()
    {
        byte[] content = System.Text.Encoding.UTF8.GetBytes("a per-file provenance document.");
        Hash128 fileRoot = ContentTierSpine.ResolveRoot(content)!.Value;
        var metadata = new FileMetadata(
            "a.txt", "docs/a.txt", content.Length, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var records = new List<ContentIngestRecord>
        {
            new(content, SourceId: fileRoot, Metadata: metadata),
        };

        var reader = new ProbeTrackingReader(present: false);
        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunAsync(
            new ListContentStream(records), new DocumentIngestHandler(layerOrder: 2),
            DefaultDocumentConfig(reader)))
            changes.Add(c);

        var managed = changes.SelectMany(c => c.Attestations).ToList();
        var markerType = Laplace.Ingestion.LayerCompletion.RelationTypeId(2);

        var marker = Assert.Single(managed, a => a.TypeId == markerType);
        Assert.Equal(fileRoot, marker.SubjectId);
        Assert.Equal(fileRoot, marker.SourceId);

        var meta = Assert.Single(managed, a => a.TypeId == FileEntity.MetadataRelationTypeId);
        Assert.Equal(fileRoot, meta.SubjectId);
        Assert.Equal(fileRoot, meta.SourceId);
        Assert.Equal(ContentTierSpine.ResolveRoot(metadata.CanonicalUtf8()), meta.ObjectId);

        // The file's content DAG landed under the file's own source id, and nothing
        // besides marker + metadata was attested (Pillar 3a).
        Assert.True(ContentEntityCount(changes) > 0);
        Assert.Equal(0, NonMarkerAttestationCount(changes));
    }

    private static IngestBatchConfig DefaultDocumentConfig(ProbeTrackingReader reader)
    {
        var config = DocumentIngestSupport.PipelineConfig("document/test", reader, batchSize: 8);
        return new IngestBatchConfig
        {
            SourceId = config.SourceId,
            BatchLabelPrefix = config.BatchLabelPrefix,
            BatchSize = config.BatchSize,
            ProbeChunkSize = 8,
            WitnessWeight = config.WitnessWeight,
            ContainmentReader = reader,
        };
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
            await foreach (var source in new DocumentMultiFileStream(dir).FilesAsync())
                labels.Add(source.FileLabel);

            Assert.Equal(2, labels.Count);
            Assert.Contains(labels, l => l.EndsWith("a.txt"));
            Assert.Contains(labels, l => l.EndsWith("b.txt"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DocumentMultiFileStream_StampsSourceIdAsFileRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-doc-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "prov.txt");
            string text = "pillar-0 provenance content.";
            await File.WriteAllTextAsync(path, text);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Hash128 expectedRoot = ContentTierSpine.ResolveRoot(bytes)
                ?? throw new InvalidOperationException("expected root");

            var records = new List<ContentIngestRecord>();
            await foreach (var source in new DocumentMultiFileStream(dir).FilesAsync())
            await foreach (var r in source.RecordsAsync())
                records.Add(r);

            var stamped = Assert.Single(records);
            Assert.Equal(expectedRoot, stamped.SourceId);
            Assert.Equal(FileEntity.SourceId(bytes), stamped.SourceId);
            Assert.NotNull(stamped.Metadata);
            Assert.Equal("prov.txt", stamped.Metadata.Value.Name);
            Assert.Equal("prov.txt", stamped.Metadata.Value.RelativePath);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DocumentMultiFileStream_ZeroByteFile_SkipsWithoutRecord()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-doc-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "empty.txt"), "");
            await File.WriteAllTextAsync(Path.Combine(dir, "ok.txt"), "non-empty document.");

            int recordCount = 0;
            await foreach (var source in new DocumentMultiFileStream(dir).FilesAsync())
            await foreach (var _ in source.RecordsAsync())
                recordCount++;

            Assert.Equal(1, recordCount);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DocumentMultiFileStream_Over2GiB_ThrowsWithReason()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-doc-huge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "huge.txt");
        try
        {
            // Sparse file: Length reports >2 GiB without allocating the bytes.
            // Point the stream at the FILE (not the dir): directory enumeration skips
            // >2MB blobs via VendoredPathFilter before OpenAsync ever runs.
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                fs.SetLength((long)int.MaxValue + 1);

            var source = Assert.Single(await CollectSources(path));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in source.RecordsAsync()) { }
            });
            Assert.Contains("2 GiB", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<List<IFileRecordSource<ContentIngestRecord>>> CollectSources(string root)
    {
        var list = new List<IFileRecordSource<ContentIngestRecord>>();
        await foreach (var s in new DocumentMultiFileStream(root).FilesAsync())
            list.Add(s);
        return list;
    }
}
