using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>One parsed grammar row — Stage 1 output after tree-sitter strip.</summary>
public readonly record struct GrammarIngestRecord(
    byte[] LineUtf8,
    GrammarAst Ast,
    int RowIndex,
    long RowsTotal);

/// <summary>
/// Grammar compose handler: probe-before-materialize, witness walk — plugs into <see cref="IngestBatchPipeline"/>.
/// </summary>
public sealed class GrammarIngestHandler : IIngestRecordHandler<GrammarIngestRecord>
{
    private readonly Hash128 _sourceId;
    private readonly string _modalityId;
    private readonly IGrammarWitness _witness;
    private readonly Hash128? _contextId;

    public GrammarIngestHandler(
        Hash128 sourceId, string modalityId, IGrammarWitness witness, Hash128? contextId = null)
    {
        _sourceId = sourceId;
        _modalityId = modalityId;
        _witness = witness;
        _contextId = contextId;
    }

    public async ValueTask<bool> TryTrunkShortcircuitAsync(
        GrammarIngestRecord record, SubstrateChangeBuilder builder, ISubstrateReader reader,
        double witnessWeight, CancellationToken ct)
    {
        if (!GrammarRowComposer.TryProbeRowRoot(
                record.LineUtf8, record.Ast, _modalityId, out var rootId, out var tier) || tier < 2)
            return false;

        byte[] bm = await reader.EntitiesExistBitmapAsync([rootId], ct).ConfigureAwait(false);
        if ((bm[0] & 1) == 0) return false;

        _witness.WalkRow(
            new GrammarComposeContext(record.LineUtf8, record.Ast, rootId, null,
                JsonGrammarHelper.FindRootObjectNode(record.Ast)),
            new RowContext(record.RowIndex, record.RowsTotal, _contextId), builder);
        return true;
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

/// <summary>Streams parsed rows from a grammar file without ReadAllBytes.</summary>
public sealed class GrammarFileRecordStream : IRecordStream<GrammarIngestRecord>
{
    private readonly string _filePath;
    private readonly string _modalityId;
    private readonly Func<ReadOnlySpan<byte>, bool>? _acceptRow;

    public GrammarFileRecordStream(
        string filePath, string modalityId, Func<ReadOnlySpan<byte>, bool>? acceptRow = null)
    {
        _filePath = filePath;
        _modalityId = modalityId;
        _acceptRow = acceptRow;
    }

    public async IAsyncEnumerable<GrammarIngestRecord> RecordsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
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

                foreach (byte[] lineUtf8 in StructuredGrammarIngest.FeedRawLinesForPipeline(iter, buf, read))
                {
                    rowsTotal++;
                    if (_acceptRow is not null && !_acceptRow(lineUtf8))
                        continue;

                    if (!StructuredGrammarIngest.TryParseRowForPipeline(iter, lineUtf8, out IntPtr ast)
                        || ast == IntPtr.Zero)
                        continue;

                    yield return new GrammarIngestRecord(
                        lineUtf8, GrammarAst.Adopt(ast), rowIndex++, rowsTotal);
                }
            }
        }
        finally
        {
            if (iter != IntPtr.Zero)
                NativeInterop.GrammarRowIterFree(iter);
        }
    }
}
