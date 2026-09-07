using System.Runtime.CompilerServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class IngestProducerFailureTests
{
    private static IngestBatchConfig Config => new()
    {
        SourceId = default,
        BatchLabelPrefix = "producer-failure",
        BatchSize = 2,
        WorkingSetProbeInterval = 2,
        WorkingSet = WorkingSetMode.Enabled,
    };

    [Fact]
    public async Task SegmentedWorkerFailure_ReleasesBlockedDispatcherAndClosesSource()
    {
        var source = new OpenRecordStream();
        var expected = new InvalidDataException("segment handler failed");
        var changes = MonolithSegmenter.RunSegmentedAsync(
            source, _ => throw expected, _ => Config, 2, "faulted-source");

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => DrainAsync(changes).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(expected, error);
        Assert.True(source.Closed);
    }

    [Fact]
    public async Task FileWorkerFailure_ReleasesBlockedEnumerationAndClosesInventory()
    {
        var source = new OpenFileStream();
        var expected = new InvalidDataException("file configuration failed");
        var changes = IngestBatchPipeline.RunMultiFileAsync(
            source, _ => throw new InvalidOperationException("handler must not be reached"),
            _ => throw expected, fileWorkers: 2);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => DrainAsync(changes).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(expected, error);
        Assert.True(source.Closed);
    }

    [Fact]
    public async Task FileInventoryFailureDuringPeek_DisposesEnumerator()
    {
        var expected = new InvalidDataException("inventory failed during peek");
        var source = new OpenFileStream(expected);
        var changes = IngestBatchPipeline.RunMultiFileAsync(
            source, _ => throw new InvalidOperationException("handler must not be reached"),
            _ => Config, fileWorkers: 2);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => DrainAsync(changes).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(expected, error);
        Assert.True(source.Closed);
    }

    private static async Task DrainAsync(IAsyncEnumerable<SubstrateChange> changes)
    {
        await foreach (var change in changes)
            change.ApplyEnvelope?.Dispose();
    }

    private sealed class OpenRecordStream : IRecordStream<int>
    {
        internal bool Closed;

        public async IAsyncEnumerable<int> RecordsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                for (int i = 0; ; ++i)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return i;
                    await Task.Yield();
                }
            }
            finally { Closed = true; }
        }
    }

    private sealed class OpenFileStream(Exception? failure = null) : IMultiFileRecordStream<int>
    {
        internal bool Closed;

        public async IAsyncEnumerable<IFileRecordSource<int>> FilesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                for (int i = 0; ; ++i)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return new DelegateFileRecordSource<int>(
                        $"file-{i}", token => new OpenRecordStream().RecordsAsync(token));
                    if (failure is not null) throw failure;
                    await Task.Yield();
                }
            }
            finally { Closed = true; }
        }
    }
}
