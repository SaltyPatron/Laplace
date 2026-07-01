using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public readonly record struct GrammarIngestRecord(
    byte[] LineUtf8,
    GrammarAst Ast,
    int RowIndex,
    long RowsTotal);

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

    public ValueTask<bool> TryTrunkShortcircuitAsync(
        GrammarIngestRecord record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct)
    {
        if (!_witness.TrunkShortcircuitWithoutCompose)
            return ValueTask.FromResult(false);

        if (!GrammarRowComposer.TryProbeRowRoot(
                record.LineUtf8, record.Ast, _modalityId, out var rootId, out var tier) || tier < 2)
            return ValueTask.FromResult(false);



        if (!reader.IsProvenPresent(rootId))
            return ValueTask.FromResult(false);

        WalkWitnessWithoutCompose(record, rootId, builder);
        return ValueTask.FromResult(true);
    }

    internal void WalkWitnessWithoutCompose(
        GrammarIngestRecord record, Hash128 rootId, SubstrateChangeBuilder builder)
    {
        _witness.WalkRow(
            new GrammarComposeContext(record.LineUtf8, record.Ast, rootId, null,
                JsonGrammarHelper.FindRootObjectNode(record.Ast)),
            new RowContext(record.RowIndex, record.RowsTotal, _contextId), builder);
    }

    public IIngestDeferredUnit CreateDeferredUnit(GrammarIngestRecord record) =>
        new GrammarDeferredUnit(record.LineUtf8, record.Ast, _sourceId, _modalityId);

    public void WalkWitness(GrammarIngestRecord record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
        GrammarRowComposer? composer = unit is GrammarIngestHandler.GrammarDeferredUnit gd ? gd.Composer : null;
        _witness.WalkRow(
            new GrammarComposeContext(record.LineUtf8, record.Ast, root, composer,
                JsonGrammarHelper.FindRootObjectNode(record.Ast)),
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
        if (recipe == IntPtr.Zero) yield break;

        IntPtr iter = StructuredGrammarIngest.CreateRowIterForPipeline(recipe);
        if (iter == IntPtr.Zero) yield break;

        int rowIndex = 0;
        long rowsTotal = 0;

        try
        {
            await using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4 << 20, useAsync: true);
            var buf = new byte[4 << 20];
            int read;
            bool eof = false;

            while (!eof)
            {
                read = await fs.ReadAsync(buf, ct);
                if (read <= 0) { eof = true; read = 0; }
                ct.ThrowIfCancellationRequested();

                if (_acceptRow is null)
                {

                    foreach (var (lineUtf8, ast) in StructuredGrammarIngest.FeedAndParseForPipeline(iter, buf, read))
                    {
                        rowsTotal++;
                        if (ast == IntPtr.Zero) continue;
                        yield return new GrammarIngestRecord(
                            lineUtf8, GrammarAst.Adopt(ast), rowIndex++, rowsTotal);
                    }
                }
                else
                {

                    foreach (byte[] lineUtf8 in StructuredGrammarIngest.FeedRawLinesForPipeline(iter, buf, read))
                    {
                        rowsTotal++;
                        if (!_acceptRow(lineUtf8))
                            continue;
                        if (!StructuredGrammarIngest.TryParseRowForPipeline(iter, lineUtf8, out IntPtr ast)
                            || ast == IntPtr.Zero)
                            continue;
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
        if (recipe == IntPtr.Zero) yield break;

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

            GrammarAst ast;
            try
            {
                ast = GrammarDecomposer.Parse(lineUtf8, recipe);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            yield return new GrammarIngestRecord(lineUtf8, ast, rowIndex++, rowsTotal);
        }
    }
}
