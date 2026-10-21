using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class ParallelLineParseFailureTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "laplace-lines-" + Guid.NewGuid().ToString("N") + ".txt");
    public ParallelLineParseFailureTests() => File.WriteAllLines(_path, Enumerable.Range(0, 200).Select(value => value.ToString()));
    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task TheOriginalParserFailureSurvivesSiblingCancellation()
    {
        var expected = new InvalidDataException("line-parser-fixture", new IOException("line-parser-inner"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Drain(
            ParallelLineParse.RecordsAsync<string>(_path, _ => throw expected, 3)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, error);
    }

    [Fact]
    public async Task UnrequestedParserCancellationCannotTurnIntoSuccess()
    {
        var expected = new OperationCanceledException("unexpected-line-parser-fixture");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(
            ParallelLineParse.RecordsAsync<string>(_path, _ => throw expected, 2)).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, error.InnerException);
    }

    [Fact]
    public async Task MissingInputRetainsTheActualFileFailure()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Drain(
            ParallelLineParse.RecordsAsync(_path + ".missing", bytes => System.Text.Encoding.UTF8.GetString(bytes), 3))
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task EarlyConsumerExitWaitsForTheParserItStarted()
    {
        using var parserEntered = new ManualResetEventSlim();
        using var releaseParser = new ManualResetEventSlim();
        int calls = 0, active = 0;
        var enumerator = ParallelLineParse.RecordsAsync(_path, bytes =>
        {
            int ordinal = Interlocked.Increment(ref calls);
            if (ordinal != 1)
            {
                Interlocked.Increment(ref active);
                parserEntered.Set();
                try
                {
                    if (!releaseParser.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("line parser was not released");
                }
                finally { Interlocked.Decrement(ref active); }
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }, 2).GetAsyncEnumerator();
        Task? disposal = null;
        try
        {
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(parserEntered.Wait(TimeSpan.FromSeconds(5)));
            disposal = enumerator.DisposeAsync().AsTask();
            await Task.Delay(100);
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            releaseParser.Set();
            await (disposal ?? enumerator.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(
            ParallelLineParse.RecordsAsync(_path, bytes => System.Text.Encoding.UTF8.GetString(bytes), 2, cancelled.Token))
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AllValidRecordsArePreserved()
    {
        var records = new List<int>();
        await foreach (var record in ParallelLineParse.RecordsAsync(_path, bytes => System.Text.Encoding.UTF8.GetString(bytes), 3))
            records.Add(int.Parse(record));
        Assert.Equal(Enumerable.Range(0, 200), records.Order());
    }

    private static async Task Drain(IAsyncEnumerable<string> records)
    {
        await foreach (var _ in records) { }
    }
}
