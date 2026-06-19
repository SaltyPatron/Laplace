using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class StructuredGrammarIngest
{
    public static async IAsyncEnumerable<SubstrateChange> IngestFileAsync(
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
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int workers = composeWorkers > 0 ? composeWorkers : ResolveComposeWorkers();
        if (workers <= 1)
        {
            await foreach (var change in IngestFileSerialAsync(
                filePath, modalityId, sourceId, witness, batchSize, witnessWeight,
                batchLabelPrefix, reportUnits, contextId, commitEpoch, acceptRow, ct))
                yield return change;
            yield break;
        }

        await foreach (var change in IngestFileParallelAsync(
            filePath, modalityId, sourceId, witness, batchSize, witnessWeight,
            batchLabelPrefix, reportUnits, contextId, commitEpoch, acceptRow, workers, ct))
            yield return change;
    }

    private static int ResolveComposeWorkers()
    {
        string? env = Environment.GetEnvironmentVariable("LAPLACE_INGEST_WORKERS");
        if (int.TryParse(env, out int w) && w > 1) return w;
        return 1;
    }

    private static async IAsyncEnumerable<SubstrateChange> IngestFileSerialAsync(
        string filePath,
        string modalityId,
        Hash128 sourceId,
        IGrammarWitness witness,
        int batchSize,
        double witnessWeight,
        string batchLabelPrefix,
        Action<long>? reportUnits,
        Hash128? contextId,
        int commitEpoch,
        Func<ReadOnlySpan<byte>, bool>? acceptRow,
        [EnumeratorCancellation] CancellationToken ct)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(modalityId);
        if (recipe == IntPtr.Zero) yield break;

        IntPtr iter = CreateRowIter(recipe);
        if (iter == IntPtr.Zero) yield break;

        try
        {
            var b = NewBuilder(sourceId, batchLabelPrefix, 0, batchSize, commitEpoch);
            int inBatch = 0, bn = 0, rowIndex = 0;
            long rowsTotal = 0;
            long rowsInBatch = 0;

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 1 << 20, useAsync: true);
            var buf = new byte[1 << 20];
            int read;
            while ((read = await fs.ReadAsync(buf, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var row in FeedRawLines(iter, buf, read))
                {
                    rowsTotal++;
                    if (acceptRow is not null && !acceptRow(row.LineUtf8))
                        continue;

                    if (!TryParseRow(iter, row.LineUtf8, out IntPtr ast) || ast == IntPtr.Zero)
                        continue;

                    rowsInBatch++;
                    using var astHandle = GrammarAst.Adopt(ast);
                    ComposeRow(row.LineUtf8, astHandle, sourceId, modalityId, witness, witnessWeight,
                        contextId, rowIndex++, rowsTotal, b);

                    if (reportUnits is not null && rowsTotal % 100 == 0)
                        reportUnits(rowsTotal);

                    if (++inBatch >= batchSize)
                    {
                        yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
                        bn++;
                        b = NewBuilder(sourceId, batchLabelPrefix, bn, batchSize, commitEpoch);
                        inBatch = 0;
                        rowsInBatch = 0;
                    }
                }

                reportUnits?.Invoke(rowsTotal);
            }

            if (inBatch > 0)
                yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
        }
        finally
        {
            if (iter != IntPtr.Zero)
                NativeInterop.GrammarRowIterFree(iter);
        }
    }

    private static async IAsyncEnumerable<SubstrateChange> IngestFileParallelAsync(
        string filePath,
        string modalityId,
        Hash128 sourceId,
        IGrammarWitness witness,
        int batchSize,
        double witnessWeight,
        string batchLabelPrefix,
        Action<long>? reportUnits,
        Hash128? contextId,
        int commitEpoch,
        Func<ReadOnlySpan<byte>, bool>? acceptRow,
        int workers,
        [EnumeratorCancellation] CancellationToken ct)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(modalityId);
        if (recipe == IntPtr.Zero) yield break;

        var workChannel = Channel.CreateBounded<ComposeWork>(
            new BoundedChannelOptions(workers * 8)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        var outChannel = Channel.CreateUnbounded<SubstrateChange>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });

        var producer = Task.Run(async () =>
        {
            IntPtr lineIter = CreateRowIter(recipe);
            if (lineIter == IntPtr.Zero)
            {
                workChannel.Writer.TryComplete();
                return;
            }
            try
            {
                long seq = 0;
                await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: 1 << 20, useAsync: true);
                var buf = new byte[1 << 20];
                int read;
                while ((read = await fs.ReadAsync(buf, ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var row in FeedRawLines(lineIter, buf, read))
                    {
                        if (acceptRow is not null && !acceptRow(row.LineUtf8))
                            continue;
                        await workChannel.Writer.WriteAsync(
                            new ComposeWork(seq++, row.LineUtf8), ct);
                    }
                }
            }
            catch (Exception ex)
            {
                workChannel.Writer.TryComplete(ex);
                return;
            }
            finally
            {
                NativeInterop.GrammarRowIterFree(lineIter);
                workChannel.Writer.TryComplete();
            }
        }, ct);

        var consumerTasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            int workerId = w;
            consumerTasks[w] = Task.Run(async () =>
            {
                IntPtr parseIter = CreateRowIter(recipe);
                if (parseIter == IntPtr.Zero)
                    return;

                var b = NewBuilder(sourceId, batchLabelPrefix, workerId, batchSize, commitEpoch);
                int inBatch = 0;
                long rowsInBatch = 0;
                int rowIndex = workerId * 1_000_000;

                try
                {
                    while (await workChannel.Reader.WaitToReadAsync(ct))
                    {
                        while (workChannel.Reader.TryRead(out var work))
                        {
                            if (!TryParseRow(parseIter, work.LineUtf8, out IntPtr ast) || ast == IntPtr.Zero)
                                continue;

                            using var astHandle = GrammarAst.Adopt(ast);
                            ComposeRow(work.LineUtf8, astHandle, sourceId, modalityId, witness, witnessWeight,
                                contextId, rowIndex++, work.Sequence + 1, b);

                            rowsInBatch++;
                            if (++inBatch >= batchSize)
                            {
                                await outChannel.Writer.WriteAsync(
                                    b.SetInputUnitsConsumed(rowsInBatch).Build(), ct);
                                rowIndex = workerId * 1_000_000;
                                b = NewBuilder(sourceId, batchLabelPrefix, workerId * 1000 + inBatch, batchSize, commitEpoch);
                                inBatch = 0;
                                rowsInBatch = 0;
                            }
                        }
                    }

                    if (inBatch > 0)
                        await outChannel.Writer.WriteAsync(
                            b.SetInputUnitsConsumed(rowsInBatch).Build(), ct);
                }
                finally
                {
                    NativeInterop.GrammarRowIterFree(parseIter);
                }
            }, ct);
        }

        var closer = Task.Run(async () =>
        {
            await producer;
            await Task.WhenAll(consumerTasks);
            outChannel.Writer.TryComplete();
        }, ct);

        long rowsReported = 0;
        while (await outChannel.Reader.WaitToReadAsync(ct))
        {
            while (outChannel.Reader.TryRead(out var change))
            {
                rowsReported += change.Metadata.InputUnitsConsumed;
                reportUnits?.Invoke(rowsReported);
                yield return change;
            }
        }

        await closer;
        if (producer.IsFaulted)
            throw producer.Exception!.GetBaseException();
    }

    private static void ComposeRow(
        byte[] lineUtf8,
        GrammarAst ast,
        Hash128 sourceId,
        string modalityId,
        IGrammarWitness witness,
        double witnessWeight,
        Hash128? contextId,
        int rowIndex,
        long rowsTotal,
        SubstrateChangeBuilder b)
    {
        using var composer = new GrammarRowComposer(lineUtf8, ast, sourceId, modalityId);
        var (ents, phys, atts, root) = composer.Materialize(witnessWeight);

        foreach (var e in ents) b.AddEntity(e);
        foreach (var p2 in phys) b.AddPhysicality(p2);
        foreach (var a in atts) b.AddAttestation(a);

        witness.WalkRow(
            new GrammarComposeContext(lineUtf8, ast, root, composer,
                JsonGrammarHelper.FindRootObjectNode(ast)),
            new RowContext(rowIndex, rowsTotal, contextId),
            b);
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
    private readonly record struct ComposeWork(long Sequence, byte[] LineUtf8);

    private static SubstrateChangeBuilder NewBuilder(
        Hash128 sourceId, string prefix, int bn, int batchSize, int commitEpoch) =>
        new SubstrateChangeBuilder(sourceId, $"{prefix}/{bn}", null,
            entityCapacity: batchSize * 32,
            physicalityCapacity: batchSize * 32,
            attestationCapacity: batchSize * 8)
            .SetCommitEpoch(commitEpoch);

    public static async Task<SubstrateChange?> IngestJsonDocumentAsync(
        string filePath,
        string modalityId,
        Hash128 sourceId,
        IGrammarWitness witness,
        double witnessWeight,
        string batchLabel,
        CancellationToken ct = default)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(modalityId);
        if (recipe == IntPtr.Zero) return null;

        byte[] utf8 = await File.ReadAllBytesAsync(filePath, ct);
        if (utf8.Length == 0) return null;

        using var ast = GrammarDecomposer.Parse(utf8, recipe);
        using var composer = new GrammarRowComposer(utf8, ast, sourceId, modalityId);
        var (ents, phys, atts, root) = composer.Materialize(witnessWeight);

        var b = NewBuilder(sourceId, batchLabel, 0, 1, commitEpoch: 0);
        foreach (var e in ents) b.AddEntity(e);
        foreach (var p in phys) b.AddPhysicality(p);
        foreach (var a in atts) b.AddAttestation(a);

        var ctx = new GrammarComposeContext(utf8, ast, root, composer,
            JsonGrammarHelper.FindRootObjectNode(ast));
        witness.WalkRow(ctx, new RowContext(0, 1), b);
        return b.SetInputUnitsConsumed(1).Build();
    }
}
