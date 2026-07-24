using Laplace.SubstrateCRUD;
using Xunit;
using static Laplace.Decomposers.Abstractions.Tests.IngestPipelineTestHelpers;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class MultiFileFailureIsolationTests
{
    private sealed class OneBadFileStream : IMultiFileRecordStream<ContentIngestRecord>
    {
        public async IAsyncEnumerable<IFileRecordSource<ContentIngestRecord>> FilesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new DelegateFileRecordSource<ContentIngestRecord>(
                "document/good-a.txt", _ => Records("good file a."));
            yield return new DelegateFileRecordSource<ContentIngestRecord>(
                "document/broken.txt", _ => Broken());
            yield return new DelegateFileRecordSource<ContentIngestRecord>(
                "document/good-b.txt", _ => Records("good file b."));
            await Task.CompletedTask;
        }

        private static async IAsyncEnumerable<ContentIngestRecord> Records(string text)
        {
            yield return ContentRecord(text);
            await Task.CompletedTask;
        }

        private static async IAsyncEnumerable<ContentIngestRecord> Broken()
        {
            await Task.CompletedTask;
            throw new IOException("simulated unreadable document");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private static async Task<List<SubstrateChange>> RunAsync(bool isolate)
    {
        var reader = new ProbeTrackingReader(present: false);
        var changes = new List<SubstrateChange>();
        await foreach (var c in IngestBatchPipeline.RunMultiFileAsync(
            new OneBadFileStream(),
            _ => new DocumentIngestHandler(layerOrder: 2),
            label => new IngestBatchConfig
            {
                SourceId = TestSource,
                BatchLabelPrefix = label,
                BatchSize = 4,
                ProbeChunkSize = 4,
                ContainmentReader = reader,
            },
            fileWorkers: 1,
            isolateFileFailures: isolate))
            changes.Add(c);
        return changes;
    }

    [Fact]
    public async Task IsolationOn_FailedFileMarkedNotDone_OthersComplete()
    {
        var changes = await RunAsync(isolate: true);

        var failed = Assert.Single(changes, c => c.Metadata.SourceContentUnitName
            .StartsWith(IngestBatchPipeline.FileFailedUnitPrefix, StringComparison.Ordinal));
        Assert.Contains("broken.txt", failed.Metadata.SourceContentUnitName);
        Assert.Contains("simulated unreadable document", failed.Metadata.SourceContentUnitName);
        // The failure marker replaces the file's boundary and never counts as a unit.
        Assert.False(failed.CountsAsUnit);
        Assert.Equal(2, changes.Count(c => c.Metadata.SourceContentUnitName
            .StartsWith(IngestBatchPipeline.PeriodBoundaryUnitPrefix, StringComparison.Ordinal)));

        // The two good files still composed end-to-end.
        Assert.True(ContentEntityCount(changes) > 0);
        Assert.Equal(2, MarkerAttestationCount(changes));
    }

    [Fact]
    public async Task IsolationOff_FailurePropagates()
    {
        // Non-per-file lanes keep their existing behavior: a broken file aborts the run.
        await Assert.ThrowsAsync<IOException>(() => RunAsync(isolate: false));
    }
}
