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
        long maxInputUnits = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int workers = composeWorkers > 0 ? composeWorkers : ResolveComposeWorkers();
        if (workers <= 1)
        {
            await foreach (var change in IngestFileSerialAsync(
                filePath, modalityId, sourceId, witness, batchSize, witnessWeight,
                batchLabelPrefix, reportUnits, contextId, commitEpoch, acceptRow,
                maxInputUnits, ct))
                yield return change;
            yield break;
        }

        await foreach (var change in IngestFileParallelAsync(
            filePath, modalityId, sourceId, witness, batchSize, witnessWeight,
            batchLabelPrefix, reportUnits, contextId, commitEpoch, acceptRow,
            workers, maxInputUnits, ct))
            yield return change;
    }

    private static int ResolveComposeWorkers()
    {
        string? compose = Environment.GetEnvironmentVariable("LAPLACE_INGEST_COMPOSE_WORKERS");
        return int.TryParse(compose, out int cw) && cw >= 1 ? cw : 1;
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
        long maxInputUnits,
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
            long rowsParsed = 0;

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4 << 20, useAsync: true);
            var buf = new byte[4 << 20];
            int read;
            bool eof = false;
            while (!eof)
            {
                read = await fs.ReadAsync(buf, ct);
                if (read <= 0) { eof = true; read = 0; }   // final pass: flush the held-back record
                ct.ThrowIfCancellationRequested();

                foreach (var row in FeedRawLines(iter, buf, read))
                {
                    rowsTotal++;
                    if (acceptRow is not null && !acceptRow(row.LineUtf8))
                        continue;

                    if (maxInputUnits > 0 && rowsParsed >= maxInputUnits)
                    {
                        if (inBatch > 0)
                            yield return b.SetInputUnitsConsumed(rowsInBatch).Build();
                        yield break;
                    }

                    if (!TryParseRow(iter, row.LineUtf8, out IntPtr ast) || ast == IntPtr.Zero)
                        continue;

                    rowsParsed++;
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

                        if (maxInputUnits > 0 && rowsParsed >= maxInputUnits)
                            yield break;
                    }
                }

                if (read > 0) reportUnits?.Invoke(rowsTotal);
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
        long maxInputUnits,
        [EnumeratorCancellation] CancellationToken ct)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(modalityId);
        if (recipe == IntPtr.Zero) yield break;

        using var capCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runCt = capCts.Token;

        var workChannel = Channel.CreateBounded<ComposeWork>(
            new BoundedChannelOptions(workers * 8)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
        // Bounded, not unbounded: the compose workers are far faster than the downstream
        // commit consumer, so an unbounded out-channel let them race ahead and pile built
        // SubstrateChange batches (each ~hundreds of thousands of rows) in RAM — one of the two
        // measured 16 GB blowup co-causes. FullMode.Wait back-pressures compose to the commit
        // rate (the producer/workChannel chain then pauses file reads). No deadlock: the single
        // reader (the DecomposeAsync enumerator) always drains as the runner commits.
        var outChannel = Channel.CreateBounded<SubstrateChange>(
            new BoundedChannelOptions(Math.Max(workers * 4, 8))
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

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
                bool eof = false;
                while (!eof)
                {
                    read = await fs.ReadAsync(buf, runCt);
                    if (read <= 0) { eof = true; read = 0; }   // final pass: flush the held-back record
                    runCt.ThrowIfCancellationRequested();
                    foreach (var row in FeedRawLines(lineIter, buf, read))
                    {
                        if (acceptRow is not null && !acceptRow(row.LineUtf8))
                            continue;
                        await workChannel.Writer.WriteAsync(
                            new ComposeWork(seq++, row.LineUtf8), runCt);
                    }
                }
            }
            catch (OperationCanceledException) when (capCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                workChannel.Writer.TryComplete();
                return;
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
        }, runCt);

        var consumerTasks = new Task[workers];
        long parsedCount = 0;
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
                    while (await workChannel.Reader.WaitToReadAsync(runCt))
                    {
                        while (workChannel.Reader.TryRead(out var work))
                        {
                            if (maxInputUnits > 0 && Volatile.Read(ref parsedCount) >= maxInputUnits)
                                continue;

                            if (!TryParseRow(parseIter, work.LineUtf8, out IntPtr ast) || ast == IntPtr.Zero)
                                continue;

                            if (!TryClaimParsedRow(maxInputUnits, ref parsedCount))
                                continue;

                            using var astHandle = GrammarAst.Adopt(ast);
                            ComposeRow(work.LineUtf8, astHandle, sourceId, modalityId, witness, witnessWeight,
                                contextId, rowIndex++, work.Sequence + 1, b);

                            rowsInBatch++;
                            if (++inBatch >= batchSize)
                            {
                                await outChannel.Writer.WriteAsync(
                                    b.SetInputUnitsConsumed(rowsInBatch).Build(), runCt);
                                rowIndex = workerId * 1_000_000;
                                b = NewBuilder(sourceId, batchLabelPrefix, workerId * 1000 + inBatch, batchSize, commitEpoch);
                                inBatch = 0;
                                rowsInBatch = 0;
                            }
                        }
                    }

                    if (inBatch > 0)
                        await outChannel.Writer.WriteAsync(
                            b.SetInputUnitsConsumed(rowsInBatch).Build(), runCt);
                }
                catch (OperationCanceledException) when (capCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    if (inBatch > 0)
                        await outChannel.Writer.WriteAsync(
                            b.SetInputUnitsConsumed(rowsInBatch).Build(), CancellationToken.None);
                }
                finally
                {
                    NativeInterop.GrammarRowIterFree(parseIter);
                }
            }, runCt);
        }

        // Must always complete the channel, even on producer/consumer fault -- otherwise the exception
        // is swallowed into this unobserved closer task, the channel never signals done, and the
        // consumer's WaitToReadAsync below hangs forever with zero CPU and no error surfaced anywhere.
        var closer = Task.Run(async () =>
        {
            await producer;
            await Task.WhenAll(consumerTasks);
        }, runCt).ContinueWith(t => outChannel.Writer.TryComplete(t.Exception), TaskScheduler.Default);

        long rowsReported = 0;
        bool capped = false;
        while (await outChannel.Reader.WaitToReadAsync(ct))
        {
            while (outChannel.Reader.TryRead(out var change))
            {
                rowsReported += change.Metadata.InputUnitsConsumed;
                reportUnits?.Invoke(rowsReported);
                yield return change;

                if (maxInputUnits > 0 && rowsReported >= maxInputUnits && !capped)
                {
                    capped = true;
                    capCts.Cancel();
                }
            }
        }

        await closer;
        if (producer.IsFaulted)
            throw producer.Exception!.GetBaseException();
    }

    private static bool TryClaimParsedRow(long maxInputUnits, ref long parsedCount)
    {
        if (maxInputUnits <= 0)
        {
            Interlocked.Increment(ref parsedCount);
            return true;
        }

        while (true)
        {
            long cur = Volatile.Read(ref parsedCount);
            if (cur >= maxInputUnits)
                return false;
            if (Interlocked.CompareExchange(ref parsedCount, cur + 1, cur) == cur)
                return true;
        }
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
        // Native-direct: compose entities + physicalities drain straight into the builder's native
        // ContentStage (no managed EntityRow/PhysicalityRow marshal — the measured client-bound
        // cost), deduped within the batch via the builder's shared seen-set; PRECEDES ride managed.
        var root = composer.DrainInto(b, witnessWeight);

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
            // Compose entities/physicalities drain into the native ContentStage now, not these
            // managed arrays — so capacity is sized for witness rows + PRECEDES, not the old
            // batchSize*32 fanout (the second measured 16 GB blowup co-cause).
            entityCapacity: batchSize,
            physicalityCapacity: batchSize,
            attestationCapacity: batchSize * 4)
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
