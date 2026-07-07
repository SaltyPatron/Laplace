using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class StructuredGrammarIngest
{
    public static IAsyncEnumerable<SubstrateChange> IngestFileAsync(
        string filePath,
        string modalityId,
        Hash128 sourceId,
        IGrammarWitness witness,
        int batchSize,
        double witnessWeight,
        string batchLabelPrefix,
        Action<long>? reportUnits,
        IngestSourceProfile sizingProfile,
        Hash128? contextId = null,
        int commitEpoch = 0,
        Func<ReadOnlySpan<byte>, bool>? acceptRow = null,
        long maxInputUnits = 0,
        ISubstrateReader? containmentReader = null,
        GrammarRecordFraming recordFraming = GrammarRecordFraming.Grammar,
        CancellationToken ct = default)
    {
        var stream = new GrammarFileRecordStream(filePath, modalityId, acceptRow, recordFraming);
        var handler = new GrammarIngestHandler(sourceId, modalityId, witness, contextId);
        var sized = IngestSizing.ResolveForSource(
            sizingProfile, batchSize > 0 ? batchSize : null);
        var config = new IngestBatchConfig
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = sized.RecordBatchSize,
            ProbeChunkSize = sized.ProbeChunkSize,
            WitnessWeight = witnessWeight,
            CommitEpoch = commitEpoch,
            ContainmentReader = containmentReader,
            ReportUnits = reportUnits,
            MaxInputUnits = maxInputUnits,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = sized.WorkingSetProbeInterval,
            WorkingSetRecordCap = sized.WorkingSetRecordCap,
            WorkingSetProfile = sizingProfile,
        };

        return IngestBatchPipeline.RunAsync(stream, handler, config, ct);
    }

    /// <summary>
    /// Record-level parallel parse for monolithic files, then the same
    /// IngestBatchPipeline + working-set spine as IngestFileAsync. Parse
    /// fans out across P-cores; existence/dedup/COPY stay on the shared lane.
    /// </summary>
    public static IAsyncEnumerable<SubstrateChange> IngestFileParallelAsync(
        string filePath,
        string modalityId,
        Hash128 sourceId,
        IGrammarWitness witness,
        double witnessWeight,
        string batchLabelPrefix,
        int workerCount,
        Hash128? contextId = null,
        int commitEpoch = 0,
        Func<ReadOnlySpan<byte>, bool>? acceptRow = null,
        GrammarRecordFraming recordFraming = GrammarRecordFraming.Grammar,
        int? recordsPerChange = null,
        IngestSourceProfile? sizingProfile = null,
        ISubstrateReader? containmentReader = null,
        CancellationToken ct = default)
    {
        var profile = sizingProfile ?? IngestSourceProfile.Wiktionary;
        var sized = IngestSizing.ResolveForSource(profile, recordsPerChange);
        int workers = workerCount > 0 ? workerCount : sized.ComposeWorkers;
        var stream = new ParallelGrammarFileRecordStream(
            filePath, modalityId, acceptRow, recordFraming, workers, ct);
        var handler = new GrammarIngestHandler(sourceId, modalityId, witness, contextId);
        var config = new IngestBatchConfig
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = sized.RecordBatchSize,
            ProbeChunkSize = sized.ProbeChunkSize,
            WitnessWeight = witnessWeight,
            CommitEpoch = commitEpoch,
            ContainmentReader = containmentReader,
            WorkingSet = WorkingSetMode.Enabled,
            WorkingSetProbeInterval = sized.WorkingSetProbeInterval,
            WorkingSetRecordCap = sized.WorkingSetRecordCap,
            WorkingSetProfile = profile,
        };
        return IngestBatchPipeline.RunAsync(stream, handler, config, ct);
    }

    /// <summary>Frame-only record stream: raw record bytes out, no parsing.</summary>
    internal static async IAsyncEnumerable<byte[]> RawRecordsAsync(
        string filePath,
        string modalityId,
        Func<ReadOnlySpan<byte>, bool>? acceptRow,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(modalityId);
        if (recipe == IntPtr.Zero) yield break;
        IntPtr iter = CreateRowIterForPipeline(recipe);
        if (iter == IntPtr.Zero) yield break;

        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4 << 20, useAsync: true);
            var buf = new byte[4 << 20];
            bool eof = false;
            while (!eof)
            {
                int read = await fs.ReadAsync(buf, ct);
                if (read <= 0) { eof = true; read = 0; }
                ct.ThrowIfCancellationRequested();
                foreach (byte[] lineUtf8 in FeedRawLinesForPipeline(iter, buf, read))
                {
                    if (acceptRow is not null && !acceptRow(lineUtf8)) continue;
                    yield return lineUtf8;
                }
            }
        }
        finally
        {
            if (iter != IntPtr.Zero)
                NativeInterop.GrammarRowIterFree(iter);
        }
    }

    public static IAsyncEnumerable<SubstrateChange> IngestFileAsync(
        string filePath,
        EtlSource source,
        IGrammarWitness witness,
        int batchSize,
        double witnessWeight,
        string batchLabelPrefix,
        Action<long>? reportUnits,
        Hash128? contextId = null,
        int commitEpoch = 0,
        Func<ReadOnlySpan<byte>, bool>? acceptRow = null,
        long maxInputUnits = 0,
        ISubstrateReader? containmentReader = null,
        CancellationToken ct = default)
        => IngestFileAsync(
            filePath,
            source.Modality.GrammarId,
            source.SourceId,
            witness,
            batchSize,
            witnessWeight,
            batchLabelPrefix,
            reportUnits,
            IngestSourceProfile.Wiktionary,
            contextId,
            commitEpoch,
            acceptRow,
            maxInputUnits,
            containmentReader,
            source.Modality.RecordFraming,
            ct);

    internal static unsafe IntPtr CreateRowIterForPipeline(IntPtr recipe) => CreateRowIter(recipe);

    internal static unsafe bool TryParseRowForPipeline(IntPtr iter, byte[] lineUtf8, out IntPtr ast)
        => TryParseRow(iter, lineUtf8, out ast);

    internal static List<byte[]> FeedRawLinesForPipeline(IntPtr iter, byte[] buf, int read)
    {
        var rows = FeedRawLines(iter, buf, read);
        return rows.ConvertAll(r => r.LineUtf8);
    }

    internal static unsafe List<(byte[] LineUtf8, IntPtr Ast)> FeedAndParseForPipeline(
        IntPtr iter, byte[] buf, int read)
    {
        var result = new List<(byte[], IntPtr)>();
        NativeInterop.ParsedRowNative* nativeRows = null;
        nuint rowCount = 0;
        fixed (byte* p = buf)
        {
            byte* chunk = read > 0 ? p : null;
            nuint chunkLen = (nuint)Math.Max(read, 0);
            if (NativeInterop.GrammarRowIterFeedParsed(iter, chunk, chunkLen, &nativeRows, &rowCount) != 0)
                return result;

            for (nuint ri = 0; ri < rowCount; ri++)
            {
                var row = nativeRows[ri];
                int rowLen = (int)row.RowLen.ToUInt64();
                var lineUtf8 = new ReadOnlySpan<byte>(row.RowUtf8.ToPointer(), rowLen).ToArray();
                result.Add((lineUtf8, row.Ast));
                nativeRows[ri].Ast = IntPtr.Zero;
            }
            if (nativeRows != null)
                NativeInterop.GrammarRowIterFreeRows(nativeRows, rowCount);
        }
        return result;
    }

    public static IAsyncEnumerable<SubstrateChange> IngestFileViaPipelineAsync(
        string filePath,
        string modalityId,
        Hash128 sourceId,
        IGrammarWitness witness,
        int batchSize,
        double witnessWeight,
        string batchLabelPrefix,
        Action<long>? reportUnits,
        Hash128? contextId = null,
        int commitEpoch = 0,
        Func<ReadOnlySpan<byte>, bool>? acceptRow = null,
        long maxInputUnits = 0,
        ISubstrateReader? containmentReader = null,
        GrammarRecordFraming recordFraming = GrammarRecordFraming.Grammar,
        CancellationToken ct = default)
        => IngestFileAsync(
            filePath, modalityId, sourceId, witness, batchSize, witnessWeight,
            batchLabelPrefix, reportUnits, IngestSourceProfile.Wiktionary,
            contextId, commitEpoch, acceptRow,
            maxInputUnits, containmentReader, recordFraming, ct);

    public static IAsyncEnumerable<SubstrateChange> IngestFileViaPipelineAsync(
        string filePath,
        EtlSource source,
        IGrammarWitness witness,
        int batchSize,
        double witnessWeight,
        string batchLabelPrefix,
        Action<long>? reportUnits,
        Hash128? contextId = null,
        int commitEpoch = 0,
        Func<ReadOnlySpan<byte>, bool>? acceptRow = null,
        long maxInputUnits = 0,
        ISubstrateReader? containmentReader = null,
        CancellationToken ct = default)
        => IngestFileAsync(
            filePath, source, witness, batchSize, witnessWeight, batchLabelPrefix,
            reportUnits, contextId, commitEpoch, acceptRow, maxInputUnits, containmentReader, ct);

    private static unsafe IntPtr CreateRowIter(IntPtr recipe)
    {
        IntPtr iter = IntPtr.Zero;
        return NativeInterop.GrammarRowIterNew(recipe, &iter) == 0 ? iter : IntPtr.Zero;
    }

    private static unsafe bool TryParseRow(IntPtr iter, byte[] lineUtf8, out IntPtr ast)
    {
        ast = IntPtr.Zero;
        fixed (byte* p = lineUtf8)
        {
            IntPtr outAst = IntPtr.Zero;
            if (NativeInterop.GrammarRowIterParseRow(iter, p, (nuint)lineUtf8.Length, &outAst) != 0)
                return false;
            ast = outAst;
            return ast != IntPtr.Zero;
        }
    }

    private static unsafe List<RawRow> FeedRawLines(IntPtr iter, byte[] buf, int read)
    {
        var rows = new List<RawRow>();
        NativeInterop.RawRowNative* nativeRows = null;
        nuint rowCount = 0;
        fixed (byte* p = buf)
        {
            if (NativeInterop.GrammarRowIterFeedLines(iter, p, (nuint)read, &nativeRows, &rowCount) != 0)
                return rows;

            for (nuint ri = 0; ri < rowCount; ri++)
            {
                var row = nativeRows[ri];
                int rowLen = (int)row.RowLen.ToUInt64();
                rows.Add(new RawRow(
                    new ReadOnlySpan<byte>(row.RowUtf8.ToPointer(), rowLen).ToArray()));
            }
            if (nativeRows != null)
                NativeInterop.GrammarRowIterFreeLines(nativeRows, rowCount);
        }
        return rows;
    }

    private readonly record struct RawRow(byte[] LineUtf8);

    public static async Task<SubstrateChange?> IngestJsonDocumentAsync(
        string filePath,
        string modalityId,
        Hash128 sourceId,
        IGrammarWitness witness,
        double witnessWeight,
        string batchLabel,
        ISubstrateReader? containmentReader = null,
        CancellationToken ct = default)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(modalityId);
        if (recipe == IntPtr.Zero) return null;

        // This lane parses ONE document as one grammar AST — it must hold
        // the whole record. Record-oriented multi-record files stream
        // through IngestFileAsync's row iterator instead; a giant file here
        // means a caller routed a corpus at the single-document lane.
        const long maxSingleDocumentBytes = 64L * 1024 * 1024;
        long fileLen = new FileInfo(filePath).Length;
        if (fileLen > maxSingleDocumentBytes)
            throw new InvalidOperationException(
                $"IngestJsonDocumentAsync: {filePath} is {fileLen / (1024 * 1024)}MB — the single-document "
                + "lane buffers the whole record (a document IS one grammar record). Files past 64MB must "
                + "stream record-boundary chunks through IngestFileAsync's grammar row iterator.");
        byte[] utf8 = await File.ReadAllBytesAsync(filePath, ct);
        if (utf8.Length == 0) return null;

        using var ast = GrammarDecomposer.Parse(utf8, recipe);
        if (containmentReader is not null
            && GrammarRowComposer.TryProbeRowRoot(utf8, ast, modalityId, out var rootId, out _)
            && (containmentReader.IsProvenPresent(rootId)
                || (await containmentReader.EntitiesExistBitmapAsync([rootId], ct).ConfigureAwait(false))[0] != 0))
        {
            var b = new SubstrateChangeBuilder(sourceId, batchLabel, null, 1, 1, 4)
                .SetCommitEpoch(0)
                .EnableDeferredContent(containmentReader);
            witness.WalkRow(
                new GrammarComposeContext(utf8, ast, rootId, null,
                    JsonGrammarHelper.FindRootObjectNode(ast)),
                new RowContext(0, 1), b);
            containmentReader.MarkProven([rootId]);
            return await b.SetInputUnitsConsumed(1).BuildAsync(ct);
        }

        using var composer = new GrammarRowComposer(utf8, ast, sourceId, modalityId);
        byte[]? bitmap = containmentReader is not null
            ? await composer.ProbeDescentBitmapAsync(containmentReader, ct)
            : null;
        var (ents, phys, atts, root) = composer.Materialize(witnessWeight, bitmap);

        // Deferred content routes witness-emitted anchors (CategoryAnchor/
        // ContentEmitter -> ContentWitnessBatch.TryAppendToBuilder) through
        // ContentBatch's presence probing at BuildAsync instead of staging
        // them unconditionally — without it, category anchors bypass the
        // containment reader entirely and already-present content re-stages.
        var builder = new SubstrateChangeBuilder(sourceId, batchLabel, null, 1, 1, 4)
            .SetCommitEpoch(0)
            .EnableDeferredContent(containmentReader);
        foreach (var e in ents) builder.AddEntity(e);
        foreach (var p in phys) builder.AddPhysicality(p);
        foreach (var a in atts) builder.AddAttestation(a);

        witness.WalkRow(
            new GrammarComposeContext(utf8, ast, root, composer,
                JsonGrammarHelper.FindRootObjectNode(ast)),
            new RowContext(0, 1), builder);
        return await builder.SetInputUnitsConsumed(1).BuildAsync(ct);
    }
}
