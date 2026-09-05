using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class DocumentFileExtractFailureTests
{
    [Fact]
    public async Task EmptyAdmittedFile_FailsExplicitly()
    {
        string file = await WriteTempFileAsync(Array.Empty<byte>());
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => ReadAllAsync(DocumentFileExtract.OpenAsync(file, "empty.txt", default)));
            Assert.Contains("empty.txt", error.Message);
            Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task InvalidUtf8AdmittedFile_FailsExplicitly()
    {
        string file = await WriteTempFileAsync([0xFF, 0xFE, (byte)'a']);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => ReadAllAsync(DocumentFileExtract.OpenAsync(file, "bad.txt", default)));
            Assert.Contains("bad.txt", error.Message);
            Assert.Contains("invalid UTF-8", error.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task MixedDirectory_FailedFilesAreMarkedAndValidFilesStillCompose()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"laplace-document-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a-good.txt"), "first valid document.");
            await File.WriteAllBytesAsync(Path.Combine(dir, "b-invalid.txt"), [0xFF, 0xFE]);
            await File.WriteAllBytesAsync(Path.Combine(dir, "c-empty.txt"), Array.Empty<byte>());
            await File.WriteAllTextAsync(Path.Combine(dir, "d-good.txt"), "second valid document.");

            var reader = new ProbeTrackingReader(present: false);
            var changes = new List<SubstrateChange>();
            await foreach (var change in IngestBatchPipeline.RunMultiFileAsync(
                               new DocumentMultiFileStream(dir),
                               _ => new DocumentIngestHandler(layerOrder: 2),
                               label => new IngestBatchConfig
                               {
                                   SourceId = DocumentSource.SourceId,
                                   BatchLabelPrefix = label,
                                   BatchSize = 4,
                                   ProbeChunkSize = 4,
                                   ContainmentReader = reader,
                               },
                               fileWorkers: 1,
                               isolateFileFailures: true))
                changes.Add(change);

            string[] failures = changes
                .Where(change => change.Metadata.SourceContentUnitName.StartsWith(
                    IngestBatchPipeline.FileFailedUnitPrefix, StringComparison.Ordinal))
                .Select(change => change.Metadata.SourceContentUnitName)
                .ToArray();
            Assert.Equal(2, failures.Length);
            Assert.Contains(failures, failure => failure.Contains("b-invalid.txt", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("c-empty.txt", StringComparison.Ordinal));
            Assert.Equal(2, changes.Count(change => change.Metadata.SourceContentUnitName.StartsWith(
                IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal)));
            Assert.Equal(2, MarkerAttestationCount(changes));
            Assert.True(ContentEntityCount(changes) > 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<string> WriteTempFileAsync(byte[] bytes)
    {
        string file = Path.Combine(Path.GetTempPath(), $"laplace-document-{Guid.NewGuid():N}.txt");
        await File.WriteAllBytesAsync(file, bytes);
        return file;
    }

    private static async Task ReadAllAsync(IAsyncEnumerable<ContentIngestRecord> records)
    {
        await foreach (var _ in records)
        {
        }
    }
}
