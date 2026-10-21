using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public readonly record struct GrammarIngestRecord(
    byte[] LineUtf8,
    GrammarAst Ast,
    int RowIndex,
    long RowsTotal) : IIngestResidentRecord
{
    public long ResidentInputBytes => checked((LineUtf8?.LongLength ?? 0)
        + (long)NativeInterop.AstResidentBytes(Ast.Handle));
}

public sealed class GrammarIngestHandler : IIngestRecordHandler<GrammarIngestRecord>
{
    private readonly Hash128 _sourceId;
    private readonly string _modalityId;
    private readonly IGrammarWitness _witness;
    private readonly Hash128? _contextId;

    internal string ModalityId => _modalityId;

    public GrammarIngestHandler(
        Hash128 sourceId, string modalityId, IGrammarWitness witness, Hash128? contextId = null)
    {
        _sourceId = sourceId;
        _modalityId = modalityId;
        _witness = witness;
        _contextId = contextId;
    }

    internal void WalkWitnessWithoutCompose(
        GrammarIngestRecord record, Hash128 rootId, SubstrateChangeBuilder builder)
    {
        _witness.WalkRow(
            new GrammarComposeContext(record.LineUtf8, record.Ast, rootId, null),
            new RowContext(record.RowIndex, record.RowsTotal, _contextId), builder);
    }

    public IIngestDeferredUnit CreateDeferredUnit(GrammarIngestRecord record) =>
        new GrammarDeferredUnit(record.LineUtf8, record.Ast, _sourceId, _modalityId);

    public void WalkWitness(GrammarIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        GrammarRowComposer? composer = unit is GrammarIngestHandler.GrammarDeferredUnit gd ? gd.Composer : null;
        _witness.WalkRow(
            new GrammarComposeContext(record.LineUtf8, record.Ast, root, composer),
            new RowContext(record.RowIndex, record.RowsTotal, _contextId), builder);
    }

    internal sealed class GrammarDeferredUnit : IIngestDeferredUnit
    {
        private readonly GrammarRowComposer _composer;
        private readonly GrammarAst _ast;
        private bool _disposed;

        public GrammarDeferredUnit(byte[] utf8, GrammarAst ast, Hash128 sourceId, string modalityId)
        {
            _ast = ast;
            _composer = new GrammarRowComposer(utf8, ast, sourceId, modalityId);
        }

        public GrammarRowComposer Composer => _composer;

        public long ResidentBytes => _composer.ResidentBytes;

        public TierTree? TreeForBatchProbe
        {
            get
            {
                _composer.EnsureProbed();
                return TierTree.FromBorrowedHandle(_composer.BorrowedTierTree());
            }
        }

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            _composer.ProbeDescentBitmapAsync(reader, ct);

        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap) =>
            _composer.DrainInto(builder, witnessWeight, descentBitmap);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _composer.Dispose();
            _ast.Dispose();
        }
    }
}

/// <summary>
/// Retains a grammar AST only as a parser result for a vendor witness. It deliberately does not
/// content-compose the serialized row: field names, delimiters, source keys, and JSON/TSV layout
/// are an encoding of the observation unless a vendor explicitly selects them as content.
/// </summary>
public sealed class GrammarWitnessIngestHandler : IIngestRecordHandler<GrammarIngestRecord>
{
    private readonly IGrammarWitness _witness;
    private readonly Hash128? _contextId;

    public GrammarWitnessIngestHandler(IGrammarWitness witness, Hash128? contextId = null)
    {
        _witness = witness;
        _contextId = contextId;
    }

    public bool ParallelizeDeferredUnitCreation => false;

    public IIngestDeferredUnit CreateDeferredUnit(GrammarIngestRecord record) =>
        new WitnessDeferredUnit(record.Ast);

    public void WalkWitness(
        GrammarIngestRecord record,
        Hash128 root,
        SubstrateChangeBuilder builder,
        IIngestDeferredUnit unit) =>
        _witness.WalkRow(
            new GrammarComposeContext(record.LineUtf8, record.Ast, default, null),
            new RowContext(record.RowIndex, record.RowsTotal, _contextId), builder);

    private sealed class WitnessDeferredUnit(GrammarAst ast) : IIngestDeferredUnit
    {
        private GrammarAst? _ast = ast;

        public TierTree? TreeForBatchProbe => null;
        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
        public Hash128 DrainInto(
            SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap) => default;
        public void Dispose() => Interlocked.Exchange(ref _ast, null)?.Dispose();
    }
}

public sealed class GrammarFileRecordStream : IRecordStream<GrammarIngestRecord>
{
    private readonly string _filePath;
    private readonly string _modalityId;
    private readonly Func<ReadOnlySpan<byte>, bool>? _acceptRow;
    private readonly GrammarRecordFraming _recordFraming;

    public GrammarFileRecordStream(
        string filePath,
        string modalityId,
        Func<ReadOnlySpan<byte>, bool>? acceptRow = null,
        GrammarRecordFraming recordFraming = GrammarRecordFraming.Grammar)
    {
        _filePath = filePath;
        _modalityId = modalityId;
        _acceptRow = acceptRow;
        _recordFraming = recordFraming;
    }

    public static GrammarFileRecordStream ForSource(
        string filePath,
        EtlSource source,
        Func<ReadOnlySpan<byte>, bool>? acceptRow = null)
        => new(filePath, source.Modality.GrammarId, acceptRow, source.Modality.RecordFraming);

    public async IAsyncEnumerable<GrammarIngestRecord> RecordsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_recordFraming == GrammarRecordFraming.Line)
        {
            await foreach (var record in RecordsLineFramedAsync(ct))
                yield return record;
            yield break;
        }

        IntPtr recipe = GrammarDecomposer.LookupById(_modalityId);
        if (recipe == IntPtr.Zero)
            throw new InvalidOperationException($"Grammar '{_modalityId}' is unavailable for '{_filePath}'.");

        IntPtr iter = StructuredGrammarIngest.CreateRowIterForPipeline(recipe);

        int rowIndex = 0;
        long rowsTotal = 0;

        try
        {
            await using var fs = IngestIo.OpenSequentialRead(_filePath, useAsync: true);
            var buf = new byte[IngestSizing.ResolveSequentialIoBufferBytes()];
            int read;
            bool eof = false;

            while (!eof)
            {
                read = await fs.ReadAsync(buf, ct);
                if (read <= 0) { eof = true; read = 0; }
                ct.ThrowIfCancellationRequested();

                if (_acceptRow is null)
                {

                    var records = StructuredGrammarIngest.FeedAndParseForPipeline(iter, buf, read);
                    int transferred = 0;
                    try
                    {
                        while (transferred < records.Count)
                        {
                            var (lineUtf8, handle) = records[transferred];
                            var ast = GrammarAst.Adopt(handle);
                            transferred++;
                            rowsTotal++;
                            yield return new GrammarIngestRecord(lineUtf8, ast, rowIndex++, rowsTotal);
                        }
                    }
                    finally
                    {
                        // A stopped consumer never takes ownership of the rest of this native batch.
                        for (int index = transferred; index < records.Count; index++)
                            NativeInterop.AstFree(records[index].Ast);
                    }
                }
                else
                {

                    foreach (byte[] lineUtf8 in StructuredGrammarIngest.FeedRawLinesForPipeline(iter, buf, read))
                    {
                        rowsTotal++;
                        if (!_acceptRow(lineUtf8))
                            continue;
                        IntPtr ast = StructuredGrammarIngest.ParseRowForPipeline(iter, lineUtf8);
                        yield return new GrammarIngestRecord(
                            lineUtf8, GrammarAst.Adopt(ast), rowIndex++, rowsTotal);
                    }
                }
            }
        }
        finally
        {
            if (iter != IntPtr.Zero)
                NativeInterop.GrammarRowIterFree(iter);
        }
    }

    private async IAsyncEnumerable<GrammarIngestRecord> RecordsLineFramedAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(_modalityId);
        if (recipe == IntPtr.Zero)
            throw new InvalidOperationException($"Grammar '{_modalityId}' is unavailable for '{_filePath}'.");

        int rowIndex = 0;
        long rowsTotal = 0;

        await foreach (ReadOnlyMemory<byte> lineMem in StreamingUtf8LineReader.ReadLinesAsync(_filePath, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (lineMem.Length == 0) continue;

            rowsTotal++;
            byte[] lineUtf8 = lineMem.Span.ToArray();
            if (_acceptRow is not null && !_acceptRow(lineUtf8))
                continue;

            GrammarAst ast = GrammarDecomposer.Parse(lineUtf8, recipe);

            yield return new GrammarIngestRecord(lineUtf8, ast, rowIndex++, rowsTotal);
        }
    }
}

/// <summary>
/// Frames + parses a monolithic grammar file on N P-core workers, yielding
/// <see cref="GrammarIngestRecord"/> into the shared IngestBatchPipeline.
/// </summary>
public sealed class ParallelGrammarFileRecordStream : IRecordStream<GrammarIngestRecord>
{
    private readonly string _filePath;
    private readonly string _modalityId;
    private readonly Func<ReadOnlySpan<byte>, bool>? _acceptRow;
    private readonly GrammarRecordFraming _recordFraming;
    private readonly int _workerCount;
    private readonly CancellationToken _ct;
    private readonly Action _initializeWorker;

    public ParallelGrammarFileRecordStream(string filePath, string modalityId,
        Func<ReadOnlySpan<byte>, bool>? acceptRow, GrammarRecordFraming recordFraming,
        int workerCount, CancellationToken ct)
        : this(filePath, modalityId, acceptRow, recordFraming, workerCount, ct,
            CpuTopology.RequirePerformanceCorePin) { }

    internal ParallelGrammarFileRecordStream(string filePath, string modalityId,
        Func<ReadOnlySpan<byte>, bool>? acceptRow, GrammarRecordFraming recordFraming,
        int workerCount, CancellationToken ct, Action initializeWorker)
    {
        _filePath = filePath;
        _modalityId = modalityId;
        _acceptRow = acceptRow;
        _recordFraming = recordFraming;
        _workerCount = Math.Max(1, workerCount);
        _ct = ct;
        _initializeWorker = initializeWorker;
    }

    public async IAsyncEnumerable<GrammarIngestRecord> RecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(_ct, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        var runCt = stop.Token;
        caller.Token.ThrowIfCancellationRequested();
        IntPtr recipe = GrammarDecomposer.LookupById(_modalityId);
        if (recipe == IntPtr.Zero)
            throw new InvalidOperationException($"Grammar '{_modalityId}' is unavailable for '{_filePath}'.");
        var raw = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_workerCount)
        {
            SingleWriter = true, SingleReader = false, FullMode = BoundedChannelFullMode.Wait,
        });
        var parsed = Channel.CreateBounded<GrammarIngestRecord>(new BoundedChannelOptions(_workerCount)
        {
            SingleWriter = false, SingleReader = true, FullMode = BoundedChannelFullMode.Wait,
        });
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        void Failed(Exception error)
        {
            if (error is OperationCanceledException)
            {
                if (runCt.IsCancellationRequested) return;
                error = new InvalidOperationException("Grammar pipeline cancelled without caller cancellation.", error);
            }
            if (Interlocked.CompareExchange(ref failure,
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error), null) is null)
            {
                parsed.Writer.TryComplete(error);
                stop.Cancel();
            }
            else if (error is not ChannelClosedException
                && !ReferenceEquals(error, Volatile.Read(ref failure)?.SourceException))
                System.Diagnostics.Trace.TraceWarning("Additional grammar worker failure for {0}: {1}", _filePath, error);
        }
        var completions = Enumerable.Range(0, _workerCount).Select(_ =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var startedWorkers = new Thread?[_workerCount];
        int remaining = _workerCount;
        long sequence = 0;
        void Finished(TaskCompletionSource<bool> completion)
        {
            if (Interlocked.Decrement(ref remaining) == 0)
                parsed.Writer.TryComplete(Volatile.Read(ref failure)?.SourceException);
            completion.TrySetResult(true);
        }
        var feeder = Task.Run(async () =>
        {
            try
            {
                var input = _recordFraming == GrammarRecordFraming.Line
                    ? LinesAsync(runCt)
                    : StructuredGrammarIngest.RawRecordsAsync(_filePath, _modalityId, _acceptRow, runCt);
                await foreach (var bytes in input.ConfigureAwait(false))
                    await raw.Writer.WriteAsync(bytes, runCt).ConfigureAwait(false);
            }
            catch (Exception error) { Failed(error); }
            finally { raw.Writer.TryComplete(); }
        }, CancellationToken.None);

        try
        {
            for (int index = 0; index < _workerCount; index++)
            {
                var completion = completions[index];
                try
                {
                    var worker = new Thread(() =>
                    {
                        IntPtr iterator = IntPtr.Zero;
                        try
                        {
                            runCt.ThrowIfCancellationRequested();
                            _initializeWorker();
                            if (_recordFraming != GrammarRecordFraming.Line)
                                iterator = StructuredGrammarIngest.CreateRowIterForPipeline(recipe);
                            while (raw.Reader.WaitToReadAsync(runCt).AsTask().GetAwaiter().GetResult())
                            while (raw.Reader.TryRead(out var bytes))
                            {
                                runCt.ThrowIfCancellationRequested();
                                GrammarAst ast;
                                if (_recordFraming == GrammarRecordFraming.Line)
                                    ast = GrammarDecomposer.Parse(bytes, recipe);
                                else
                                {
                                    var handle = StructuredGrammarIngest.ParseRowForPipeline(iterator, bytes);
                                    ast = GrammarAst.Adopt(handle);
                                }
                                long row = Interlocked.Increment(ref sequence);
                                try
                                {
                                    parsed.Writer.WriteAsync(new GrammarIngestRecord(bytes, ast,
                                        checked((int)row - (_recordFraming == GrammarRecordFraming.Line ? 1 : 0)), row),
                                        runCt).AsTask().GetAwaiter().GetResult();
                                }
                                catch { ast.Dispose(); throw; }
                            }
                        }
                        catch (Exception error) { Failed(error); }
                        finally
                        {
                            try { if (iterator != IntPtr.Zero) NativeInterop.GrammarRowIterFree(iterator); }
                            catch (Exception error) { Failed(error); }
                            Finished(completion);
                        }
                    }) { IsBackground = true, Name = $"grammar-parse-pcore-{index}" };
                    worker.Start();
                    startedWorkers[index] = worker;
                }
                catch (Exception error) { Failed(error); Finished(completion); }
            }
            await foreach (var record in parsed.Reader.ReadAllAsync(caller.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref failure) is { } rejected)
                {
                    record.Ast.Dispose();
                    rejected.Throw();
                }
                yield return record;
            }
            caller.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            stop.Cancel();
            await feeder.ConfigureAwait(false);
            await Task.WhenAll(completions.Select(item => item.Task)).ConfigureAwait(false);
            foreach (var worker in startedWorkers)
                if (worker is not null && !worker.Join(TimeSpan.FromSeconds(5)))
                    Failed(new TimeoutException($"Grammar worker '{worker.Name}' did not exit after completing cleanup."));
            while (parsed.Reader.TryRead(out var abandoned)) abandoned.Ast.Dispose();
            Volatile.Read(ref failure)?.Throw();
        }
    }

    private async IAsyncEnumerable<byte[]> LinesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var memory in StreamingUtf8LineReader.ReadLinesAsync(_filePath, ct).ConfigureAwait(false))
        {
            if (memory.Length == 0) continue;
            var bytes = memory.Span.ToArray();
            if (_acceptRow is null || _acceptRow(bytes)) yield return bytes;
        }
    }
}
