using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

[Collection("GrammarPerfcache")]
public sealed class GrammarStreamFailureTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "laplace-grammar-failure-" + Guid.NewGuid().ToString("N") + ".tsv");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(GrammarRecordFraming.Line)]
    [InlineData(GrammarRecordFraming.Grammar)]
    public async Task MissingFileIsNotSuccessfulEmptyParallelInput(GrammarRecordFraming framing)
    {
        var stream = Stream(framing);
        await Assert.ThrowsAsync<FileNotFoundException>(() => Drain(stream).WaitAsync(Deadline));
    }

    [Theory]
    [InlineData(GrammarRecordFraming.Line)]
    [InlineData(GrammarRecordFraming.Grammar)]
    public async Task FeederFailureReachesCallerAndStopsWorkers(GrammarRecordFraming framing)
    {
        await WriteRows();
        var stream = new ParallelGrammarFileRecordStream(_path, "tsv",
            _ => throw new FormatException("grammar-feeder-fixture"), framing, 2, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<FormatException>(() => Drain(stream).WaitAsync(Deadline));
        Assert.Equal("grammar-feeder-fixture", failure.Message);
    }

    [Theory]
    [InlineData(GrammarRecordFraming.Line)]
    [InlineData(GrammarRecordFraming.Grammar)]
    public async Task UnexpectedFeederCancellationIsAnError(GrammarRecordFraming framing)
    {
        await WriteRows();
        var stream = new ParallelGrammarFileRecordStream(_path, "tsv",
            _ => throw new OperationCanceledException("unrequested-cancel-fixture"), framing, 2, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(stream).WaitAsync(Deadline));
        Assert.IsType<OperationCanceledException>(failure.InnerException);
    }

    [Theory]
    [InlineData(GrammarRecordFraming.Line)]
    [InlineData(GrammarRecordFraming.Grammar)]
    public async Task WorkerSetupFailureReachesCallerWithoutUnhandledThreadException(GrammarRecordFraming framing)
    {
        await WriteRows();
        var stream = new ParallelGrammarFileRecordStream(_path, "tsv", null, framing, 2,
            CancellationToken.None, () => throw new IOException("grammar-worker-setup-fixture"));
        var failure = await Assert.ThrowsAsync<IOException>(() => Drain(stream).WaitAsync(Deadline));
        Assert.Equal("grammar-worker-setup-fixture", failure.Message);
    }

    [Theory]
    [InlineData(GrammarRecordFraming.Line, false)]
    [InlineData(GrammarRecordFraming.Line, true)]
    [InlineData(GrammarRecordFraming.Grammar, false)]
    [InlineData(GrammarRecordFraming.Grammar, true)]
    public async Task EarlyConsumerExitReleasesInputAndUnconsumedRecords(GrammarRecordFraming framing, bool parallel)
    {
        await WriteRows();
        IRecordStream<GrammarIngestRecord> stream = parallel ? Stream(framing)
            : new GrammarFileRecordStream(_path, "tsv", recordFraming: framing);
        async Task ReadOne()
        {
            await foreach (var record in stream.RecordsAsync())
            {
                record.Ast.Dispose();
                break;
            }
        }
        await ReadOne().WaitAsync(Deadline);
        using var exclusive = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Theory]
    [InlineData(GrammarRecordFraming.Line)]
    [InlineData(GrammarRecordFraming.Grammar)]
    public async Task CallerCancellationRemainsCancellationAndJoinsWorkers(GrammarRecordFraming framing)
    {
        await WriteRows();
        using var cancel = new CancellationTokenSource();
        var stream = new ParallelGrammarFileRecordStream(_path, "tsv", _ =>
        {
            cancel.Cancel();
            return true;
        }, framing, 2, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(stream, cancel.Token).WaitAsync(Deadline));
        using var exclusive = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(GrammarRecordFraming.Line)]
    [InlineData(GrammarRecordFraming.Grammar)]
    public async Task ParallelSuccessPreservesEveryAcceptedRecord(GrammarRecordFraming framing)
    {
        var expected = await WriteRows();
        var actual = new List<string>();
        await foreach (var record in Stream(framing).RecordsAsync())
        {
            using (record.Ast) actual.Add(Encoding.UTF8.GetString(record.LineUtf8).TrimEnd('\r', '\n'));
        }
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(GrammarRecordFraming.Line, false)]
    [InlineData(GrammarRecordFraming.Line, true)]
    [InlineData(GrammarRecordFraming.Grammar, false)]
    [InlineData(GrammarRecordFraming.Grammar, true)]
    public async Task MissingGrammarIsNotSuccessfulEmptyInput(GrammarRecordFraming framing, bool parallel)
    {
        IRecordStream<GrammarIngestRecord> stream = parallel
            ? new ParallelGrammarFileRecordStream(_path, "missing-grammar-fixture", null, framing, 2, CancellationToken.None)
            : new GrammarFileRecordStream(_path, "missing-grammar-fixture", recordFraming: framing);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var record in stream.RecordsAsync()) record.Ast.Dispose();
        });
    }

    [Fact]
    public void NativeIteratorErrorCodesCannotBecomeEmptyOrRejectedRecordResults()
    {
        byte[] row = "a\tb"u8.ToArray();
        Assert.Throws<InvalidOperationException>(() => StructuredGrammarIngest.CreateRowIterForPipeline(IntPtr.Zero));
        Assert.Throws<InvalidOperationException>(() => StructuredGrammarIngest.ParseRowForPipeline(IntPtr.Zero, row));
        Assert.Throws<InvalidOperationException>(() => StructuredGrammarIngest.FeedRawLinesForPipeline(IntPtr.Zero, row, row.Length));
        Assert.Throws<InvalidOperationException>(() => StructuredGrammarIngest.FeedAndParseForPipeline(IntPtr.Zero, row, row.Length));
    }

    private ParallelGrammarFileRecordStream Stream(GrammarRecordFraming framing) =>
        new(_path, "tsv", null, framing, 2, CancellationToken.None);

    private async Task<string[]> WriteRows()
    {
        string[] rows = Enumerable.Range(0, 200).Select(i => $"row{i}\tvalue{i}").ToArray();
        await File.WriteAllLinesAsync(_path, rows, new UTF8Encoding(false));
        return rows;
    }

    private static async Task Drain(ParallelGrammarFileRecordStream stream, CancellationToken ct = default)
    {
        await foreach (var record in stream.RecordsAsync(ct)) record.Ast.Dispose();
    }

    public void Dispose() => File.Delete(_path);
}
