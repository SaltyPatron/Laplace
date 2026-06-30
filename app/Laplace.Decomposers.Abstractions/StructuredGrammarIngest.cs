using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Grammar file ingest — single path: <see cref="GrammarFileRecordStream"/> →
/// <see cref="IngestBatchPipeline"/> (batched descent, probe-before-materialize).
/// </summary>
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
        Hash128? contextId = null,
        int commitEpoch = 0,
        Func<ReadOnlySpan<byte>, bool>? acceptRow = null,
        long maxInputUnits = 0,
        ISubstrateReader? containmentReader = null,
        GrammarRecordFraming recordFraming = GrammarRecordFraming.Grammar,
        CancellationToken ct = default)
    {
        if (IntentStage.IsBulkFreshBypass)
            containmentReader = null;

        // No containment reader: each worker's stage deduplicates within its own batch via
        // the per-stage witness set (intent_stage_witness_record). Across batches the DB
        // anti-join (apply_batch DISTINCT ON + NOT EXISTS) deduplicates without ON CONFLICT.
        // The native content stage and intent_stage are per-instance with no global mutable
        // state, so P-core-pinned workers compose into independent stages concurrently.
        // With a reader: sequential pipeline uses the descent probe to filter novel entities
        // before compose — no parallel path here, probe results are per-batch sequential.
        if (containmentReader is null)
            return IngestFileParallelAsync(
                filePath, modalityId, sourceId, witness, batchSize, witnessWeight,
                batchLabelPrefix, reportUnits, contextId, commitEpoch, acceptRow,
                maxInputUnits, recordFraming, ct);

        return IngestFileViaPipelineAsync(
            filePath, modalityId, sourceId, witness, batchSize, witnessWeight,
            batchLabelPrefix, reportUnits, contextId, commitEpoch, acceptRow,
            maxInputUnits, containmentReader, recordFraming, ct);
    }

    private static IAsyncEnumerable<SubstrateChange> IngestFileParallelAsync(
        string filePath, string modalityId, Hash128 sourceId,
        IGrammarWitness witness, int batchSize, double witnessWeight,
        string batchLabelPrefix, Action<long>? reportUnits,
        Hash128? contextId, int commitEpoch,
        Func<ReadOnlySpan<byte>, bool>? acceptRow, long maxInputUnits,
        GrammarRecordFraming recordFraming, CancellationToken ct)
    {
        var stream = new GrammarFileRecordStream(filePath, modalityId, acceptRow, recordFraming);
        var handler = new GrammarIngestHandler(sourceId, modalityId, witness, contextId);
        var config = new IngestBatchConfig
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = batchSize,
            WitnessWeight = witnessWeight,
            CommitEpoch = commitEpoch,
            ContainmentReader = null,
            ReportUnits = reportUnits,
            MaxInputUnits = maxInputUnits,
        };
        var records = maxInputUnits > 0
            ? stream.RecordsAsync(ct).Take((int)Math.Min(maxInputUnits, int.MaxValue))
            : stream.RecordsAsync(ct);
        int workers = IngestParallelism.ResolveFileWorkers(coreHeadroom: 1);
        return PCoreParallelCompose.RunAsync(records, handler, config, workers, ct);
    }

    /// <summary>Ingest one file using record framing from the manifest row.</summary>
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

    /// <summary>
    /// Single-pass: frame records from <paramref name="buf"/> AND parse each, returning bytes+AST
    /// together. Eliminates the per-record re-parse that two-pass (FeedRawLines + ParseRow) required.
    /// Pass <paramref name="read"/>=0 at end-of-stream to flush the final partial record.
    /// </summary>
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
    {
        var stream = new GrammarFileRecordStream(filePath, modalityId, acceptRow, recordFraming);
        var handler = new GrammarIngestHandler(sourceId, modalityId, witness, contextId);
        var config = new IngestBatchConfig
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = batchSize,
            ProbeChunkSize = IngestTopology.Current.Sizing.ProbeChunkSize,
            WitnessWeight = witnessWeight,
            CommitEpoch = commitEpoch,
            ContainmentReader = containmentReader,
            ReportUnits = reportUnits,
            MaxInputUnits = maxInputUnits,
        };
        return IngestBatchPipeline.RunAsync(stream, handler, config, ct);
    }

    /// <summary>Pipeline ingest using modality + record framing from the manifest row.</summary>
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
        => IngestFileViaPipelineAsync(
            filePath,
            source.Modality.GrammarId,
            source.SourceId,
            witness,
            batchSize,
            witnessWeight,
            batchLabelPrefix,
            reportUnits,
            contextId,
            commitEpoch,
            acceptRow,
            maxInputUnits,
            containmentReader,
            source.Modality.RecordFraming,
            ct);

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

    private static SubstrateChangeBuilder NewBuilder(
        Hash128 sourceId, string prefix, int bn, int batchSize, int commitEpoch,
        ISubstrateReader? containmentReader = null) =>
        new SubstrateChangeBuilder(sourceId, $"{prefix}/{bn}", null,
            entityCapacity: batchSize,
            physicalityCapacity: batchSize,
            attestationCapacity: batchSize * 4)
            .SetCommitEpoch(commitEpoch)
            .EnableDeferredContent(containmentReader);

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

        byte[] utf8 = await File.ReadAllBytesAsync(filePath, ct);
        if (utf8.Length == 0) return null;

        using var ast = GrammarDecomposer.Parse(utf8, recipe);
        using var composer = new GrammarRowComposer(utf8, ast, sourceId, modalityId);
        byte[]? bitmap = containmentReader is not null
            ? await composer.ProbeDescentBitmapAsync(containmentReader, ct)
            : null;
        var (ents, phys, atts, root) = composer.Materialize(witnessWeight, bitmap);

        var b = NewBuilder(sourceId, batchLabel, 0, 1, commitEpoch: 0, containmentReader);
        foreach (var e in ents) b.AddEntity(e);
        foreach (var p in phys) b.AddPhysicality(p);
        foreach (var a in atts) b.AddAttestation(a);

        var ctx = new GrammarComposeContext(utf8, ast, root, composer,
            JsonGrammarHelper.FindRootObjectNode(ast));
        witness.WalkRow(ctx, new RowContext(0, 1), b);
        return await b.SetInputUnitsConsumed(1).BuildAsync(ct);
    }
}
