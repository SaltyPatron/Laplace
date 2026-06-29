using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Grammar file ingest — single path: <see cref="GrammarFileRecordStream"/> →
/// <see cref="IngestBatchPipeline"/> (batched descent, probe-before-materialize).
/// </summary>
public static class StructuredGrammarIngest
{
    /// <param name="composeWorkers">Ignored. Retained for call-site compatibility; compose parallelism removed.</param>
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
        int composeWorkers = 0,
        long maxInputUnits = 0,
        ISubstrateReader? containmentReader = null,
        CancellationToken ct = default)
    {
        if (IntentStage.IsBulkFreshBypass)
            containmentReader = null;
        return IngestFileViaPipelineAsync(
            filePath, modalityId, sourceId, witness, batchSize, witnessWeight,
            batchLabelPrefix, reportUnits, contextId, commitEpoch, acceptRow,
            maxInputUnits, containmentReader, ct);
    }

    [Obsolete("Compose worker pool removed; grammar ingest is always IngestBatchPipeline.")]
    public static int ResolveComposeWorkers()
    {
        string? compose = Environment.GetEnvironmentVariable("LAPLACE_INGEST_COMPOSE_WORKERS");
        if (int.TryParse(compose, out int cw) && cw >= 1)
            return cw;
        return 1;
    }

    internal static unsafe IntPtr CreateRowIterForPipeline(IntPtr recipe) => CreateRowIter(recipe);

    internal static unsafe bool TryParseRowForPipeline(IntPtr iter, byte[] lineUtf8, out IntPtr ast)
        => TryParseRow(iter, lineUtf8, out ast);

    internal static List<byte[]> FeedRawLinesForPipeline(IntPtr iter, byte[] buf, int read)
    {
        var rows = FeedRawLines(iter, buf, read);
        return rows.ConvertAll(r => r.LineUtf8);
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
        CancellationToken ct = default)
    {
        var stream = new GrammarFileRecordStream(filePath, modalityId, acceptRow);
        var handler = new GrammarIngestHandler(sourceId, modalityId, witness, contextId);
        var config = new IngestBatchConfig
        {
            SourceId = sourceId,
            BatchLabelPrefix = batchLabelPrefix,
            BatchSize = batchSize,
            WitnessWeight = witnessWeight,
            CommitEpoch = commitEpoch,
            ContainmentReader = containmentReader,
            ReportUnits = reportUnits,
            MaxInputUnits = maxInputUnits,
        };
        return IngestBatchPipeline.RunAsync(stream, handler, config, ct);
    }

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
