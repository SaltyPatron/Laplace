using System.Runtime.CompilerServices;
using System.Threading;
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
        ISubstrateReader? containmentReader = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int workers = composeWorkers > 0 ? composeWorkers : ResolveComposeWorkers();
        if (workers <= 1)
        {
            await foreach (var change in IngestFileSerialAsync(
                filePath, modalityId, sourceId, witness, batchSize, witnessWeight,
                batchLabelPrefix, reportUnits, contextId, commitEpoch, acceptRow,
                maxInputUnits, containmentReader, ct))
                yield return change;
            yield break;
        }

        await foreach (var change in IngestFileParallelAsync(
            filePath, modalityId, sourceId, witness, batchSize, witnessWeight,
            batchLabelPrefix, reportUnits, contextId, commitEpoch, acceptRow,
            workers, maxInputUnits, containmentReader, ct))
            yield return change;
    }

    
    
    private const int ContainmentProbeChunk = 1024;

    private readonly record struct PendingRow(
        GrammarRowComposer Composer, GrammarAst Ast, byte[] LineUtf8, int RowIndex, long RowsTotal);

    
    
    
    
    
    
    private static async Task<byte[][]?> ProbeContainmentAsync(
        List<PendingRow> pending, ISubstrateReader reader, CancellationToken ct)
    {
        if (pending.Count == 0) return null;
        var perRow = new Hash128[pending.Count][];
        int total = 0;
        for (int i = 0; i < pending.Count; i++)
        {
            perRow[i] = pending[i].Composer.EntityIds();
            total += perRow[i].Length;
        }
        if (total == 0) return null;

        var candidates = new Hash128[total];
        int off = 0;
        for (int i = 0; i < perRow.Length; i++)
        {
            Array.Copy(perRow[i], 0, candidates, off, perRow[i].Length);
            off += perRow[i].Length;
        }

        byte[] combined = await reader.EntitiesExistBitmapAsync(candidates, ct);
        long combinedBits = (long)combined.Length * 8;

        var result = new byte[pending.Count][];
        int g = 0;
        for (int i = 0; i < pending.Count; i++)
        {
            int n = perRow[i].Length;
            var bm = new byte[(n + 7) / 8];
            for (int j = 0; j < n; j++)
            {
                int gi = g + j;
                if (gi < combinedBits && (combined[gi >> 3] & (1 << (gi & 7))) != 0)
                    bm[j >> 3] |= (byte)(1 << (j & 7));
            }
            result[i] = bm;
            g += n;
        }
        return result;
    }

    
    private static void DrainAndWalk(
        in PendingRow pr, Hash128 sourceId, IGrammarWitness witness, double witnessWeight,
        Hash128? contextId, SubstrateChangeBuilder b, byte[]? bitmap)
    {
        var root = pr.Composer.DrainInto(b, witnessWeight, bitmap);
        witness.WalkRow(
            new GrammarComposeContext(pr.LineUtf8, pr.Ast, root, pr.Composer,
                JsonGrammarHelper.FindRootObjectNode(pr.Ast)),
            new RowContext(pr.RowIndex, pr.RowsTotal, contextId),
            b);
    }

    private static int ResolveComposeWorkers()
    {
        string? compose = Environment.GetEnvironmentVariable("LAPLACE_INGEST_COMPOSE_WORKERS");
        if (int.TryParse(compose, out int cw) && cw >= 1)
            return cw;
        // Conservative default for a SHARED, in-use machine. The 8 P-cores (16 logical) must be split
        // across the user, the GPUs/hypervisor/WSL, Postgres's own parallel workers, AND the other
        // ingest pools (decompose file workers + commit lanes). Compose grabbing all of them starves
        // the box — the 4 was deliberate headroom, not arbitrary. Raise LAPLACE_INGEST_COMPOSE_WORKERS
        // for a dedicated run.
        return Math.Min(4, CpuTopology.ResolveCpuBoundWorkers(headroom: 2, maxCap: 8));
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
        ISubstrateReader? containmentReader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(modalityId);
        if (recipe == IntPtr.Zero) yield break;

        IntPtr iter = CreateRowIter(recipe);
        if (iter == IntPtr.Zero) yield break;

        // Two-phase tier-containment dedup: when a reader is supplied, compose rows into a bounded
        // pending buffer, batch-probe their entity ids with the existing entities_exist_bitmap, then
        // drain only the novel subtrees (MerkleDedup.TrunkShortcircuit inside GrammarRowComposer).
        // reader == null keeps the original one-pass behavior byte-for-byte.
        var pending = containmentReader is not null ? new List<PendingRow>(ContainmentProbeChunk) : null;

        try
        {
            var b = NewBuilder(sourceId, batchLabelPrefix, 0, batchSize, commitEpoch, containmentReader);
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
                        if (pending is { Count: > 0 })
                        {
                            var maps = await ProbeContainmentAsync(pending, containmentReader!, ct);
                            for (int k = 0; k < pending.Count; k++)
                            {
                                DrainAndWalk(pending[k], sourceId, witness, witnessWeight, contextId, b, maps?[k]);
                                pending[k].Composer.Dispose();
                                pending[k].Ast.Dispose();
                                rowsInBatch++;
                                if (++inBatch >= batchSize)
                                {
                                    yield return await b.SetInputUnitsConsumed(rowsInBatch).BuildAsync(ct);
                                    b = NewBuilder(sourceId, batchLabelPrefix, ++bn, batchSize, commitEpoch, containmentReader);
                                    inBatch = 0; rowsInBatch = 0;
                                }
                            }
                            pending.Clear();
                        }
                        if (inBatch > 0)
                            yield return await b.SetInputUnitsConsumed(rowsInBatch).BuildAsync(ct);
                        yield break;
                    }

                    if (!TryParseRow(iter, row.LineUtf8, out IntPtr ast) || ast == IntPtr.Zero)
                        continue;

                    rowsParsed++;
                    var astHandle = GrammarAst.Adopt(ast);

                    if (pending is null)
                    {
                        try
                        {
                            using (astHandle)
                                ComposeRow(row.LineUtf8, astHandle, sourceId, modalityId, witness, witnessWeight,
                                    contextId, rowIndex++, rowsTotal, b);
                            rowsInBatch++;
                        }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("laplace_grammar_compose"))
                        {
                            Console.Error.WriteLine(
                                $"[COMPOSE_SKIP] {filePath}:{rowsTotal} rc={ex.Message} row={System.Text.Encoding.UTF8.GetString(row.LineUtf8[..Math.Min(row.LineUtf8.Length, 200)])}");
                            continue;
                        }

                        if (reportUnits is not null && rowsTotal % 100 == 0)
                            reportUnits(rowsTotal);

                        if (++inBatch >= batchSize)
                        {
                            yield return await b.SetInputUnitsConsumed(rowsInBatch).BuildAsync(ct);
                            bn++;
                            b = NewBuilder(sourceId, batchLabelPrefix, bn, batchSize, commitEpoch, containmentReader);
                            inBatch = 0;
                            rowsInBatch = 0;

                            if (maxInputUnits > 0 && rowsParsed >= maxInputUnits)
                                yield break;
                        }
                        continue;
                    }

                    // Two-phase: compose now, defer drain/walk until the chunk is probed.
                    GrammarRowComposer composer;
                    try
                    {
                        composer = new GrammarRowComposer(row.LineUtf8, astHandle, sourceId, modalityId);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("laplace_grammar_compose"))
                    {
                        Console.Error.WriteLine(
                            $"[COMPOSE_SKIP] {filePath}:{rowsTotal} rc={ex.Message} row={System.Text.Encoding.UTF8.GetString(row.LineUtf8[..Math.Min(row.LineUtf8.Length, 200)])}");
                        astHandle.Dispose();
                        continue;
                    }
                    pending.Add(new PendingRow(composer, astHandle, row.LineUtf8, rowIndex++, rowsTotal));

                    if (reportUnits is not null && rowsTotal % 100 == 0)
                        reportUnits(rowsTotal);

                    if (pending.Count >= ContainmentProbeChunk)
                    {
                        var maps = await ProbeContainmentAsync(pending, containmentReader!, ct);
                        bool capHit = false;
                        for (int k = 0; k < pending.Count; k++)
                        {
                            DrainAndWalk(pending[k], sourceId, witness, witnessWeight, contextId, b, maps?[k]);
                            pending[k].Composer.Dispose();
                            pending[k].Ast.Dispose();
                            rowsInBatch++;
                            if (++inBatch >= batchSize)
                            {
                                yield return await b.SetInputUnitsConsumed(rowsInBatch).BuildAsync(ct);
                                b = NewBuilder(sourceId, batchLabelPrefix, ++bn, batchSize, commitEpoch, containmentReader);
                                inBatch = 0; rowsInBatch = 0;
                                if (maxInputUnits > 0 && rowsParsed >= maxInputUnits) capHit = true;
                            }
                        }
                        pending.Clear();
                        if (capHit) yield break;
                    }
                }

                if (read > 0) reportUnits?.Invoke(rowsTotal);
            }

            if (pending is { Count: > 0 })
            {
                var maps = await ProbeContainmentAsync(pending, containmentReader!, ct);
                for (int k = 0; k < pending.Count; k++)
                {
                    DrainAndWalk(pending[k], sourceId, witness, witnessWeight, contextId, b, maps?[k]);
                    pending[k].Composer.Dispose();
                    pending[k].Ast.Dispose();
                    rowsInBatch++;
                    if (++inBatch >= batchSize)
                    {
                        yield return await b.SetInputUnitsConsumed(rowsInBatch).BuildAsync(ct);
                        b = NewBuilder(sourceId, batchLabelPrefix, ++bn, batchSize, commitEpoch, containmentReader);
                        inBatch = 0; rowsInBatch = 0;
                    }
                }
                pending.Clear();
            }

            if (inBatch > 0)
                yield return await b.SetInputUnitsConsumed(rowsInBatch).BuildAsync(ct);
        }
        finally
        {
            if (pending is not null)
                foreach (var pr in pending)
                {
                    pr.Composer.Dispose();
                    pr.Ast.Dispose();
                }
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
        ISubstrateReader? containmentReader,
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

                var b = NewBuilder(sourceId, batchLabelPrefix, workerId, batchSize, commitEpoch, containmentReader);
                int inBatch = 0;
                long rowsInBatch = 0;
                int rowIndex = workerId * 1_000_000;
                var pending = containmentReader is not null ? new List<PendingRow>(ContainmentProbeChunk) : null;

                // Drains the deferred pending buffer: one batch-probe of all held entity ids, then
                // emit only the novel subtrees per row. Mutates b/inBatch/rowsInBatch/rowIndex.
                async Task DrainPendingAsync(CancellationToken fct)
                {
                    if (pending is not { Count: > 0 }) return;
                    var maps = await ProbeContainmentAsync(pending, containmentReader!, fct);
                    for (int k = 0; k < pending.Count; k++)
                    {
                        DrainAndWalk(pending[k], sourceId, witness, witnessWeight, contextId, b, maps?[k]);
                        pending[k].Composer.Dispose();
                        pending[k].Ast.Dispose();
                        rowsInBatch++;
                        if (++inBatch >= batchSize)
                        {
                            await outChannel.Writer.WriteAsync(
                                await b.SetInputUnitsConsumed(rowsInBatch).BuildAsync(fct), fct);
                            rowIndex = workerId * 1_000_000;
                            b = NewBuilder(sourceId, batchLabelPrefix, workerId * 1000 + inBatch, batchSize, commitEpoch, containmentReader);
                            inBatch = 0;
                            rowsInBatch = 0;
                        }
                    }
                    pending.Clear();
                }

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

                            var astHandle = GrammarAst.Adopt(ast);

                            if (pending is null)
                            {
                                try
                                {
                                    using (astHandle)
                                        ComposeRow(work.LineUtf8, astHandle, sourceId, modalityId, witness, witnessWeight,
                                            contextId, rowIndex++, work.Sequence + 1, b);
                                }
                                catch (InvalidOperationException ex) when (ex.Message.Contains("laplace_grammar_compose"))
                                {
                                    Console.Error.WriteLine(
                                        $"[COMPOSE_SKIP] {filePath}:{work.Sequence} rc={ex.Message} row={System.Text.Encoding.UTF8.GetString(work.LineUtf8[..Math.Min(work.LineUtf8.Length, 200)])}");
                                    continue;
                                }

                                rowsInBatch++;
                                if (++inBatch >= batchSize)
                                {
                                    await outChannel.Writer.WriteAsync(
                                        await b.SetInputUnitsConsumed(rowsInBatch).BuildAsync(runCt), runCt);
                                    rowIndex = workerId * 1_000_000;
                                    b = NewBuilder(sourceId, batchLabelPrefix, workerId * 1000 + inBatch, batchSize, commitEpoch, containmentReader);
                                    inBatch = 0;
                                    rowsInBatch = 0;
                                }
                                continue;
                            }

                            GrammarRowComposer composer;
                            try
                            {
                                composer = new GrammarRowComposer(work.LineUtf8, astHandle, sourceId, modalityId);
                            }
                            catch (InvalidOperationException ex) when (ex.Message.Contains("laplace_grammar_compose"))
                            {
                                Console.Error.WriteLine(
                                    $"[COMPOSE_SKIP] {filePath}:{work.Sequence} rc={ex.Message} row={System.Text.Encoding.UTF8.GetString(work.LineUtf8[..Math.Min(work.LineUtf8.Length, 200)])}");
                                astHandle.Dispose();
                                continue;
                            }
                            pending.Add(new PendingRow(composer, astHandle, work.LineUtf8, rowIndex++, work.Sequence + 1));
                            if (pending.Count >= ContainmentProbeChunk)
                                await DrainPendingAsync(runCt);
                        }
                    }

                    await DrainPendingAsync(runCt);
                    if (inBatch > 0)
                        await outChannel.Writer.WriteAsync(
                            await b.SetInputUnitsConsumed(rowsInBatch).BuildAsync(runCt), runCt);
                }
                catch (OperationCanceledException) when (capCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    try { await DrainPendingAsync(CancellationToken.None); } catch { /* best effort */ }
                    if (inBatch > 0)
                        await outChannel.Writer.WriteAsync(
                            await b.SetInputUnitsConsumed(rowsInBatch).BuildAsync(CancellationToken.None), CancellationToken.None);
                }
                finally
                {
                    if (pending is not null)
                        foreach (var pr in pending)
                        {
                            pr.Composer.Dispose();
                            pr.Ast.Dispose();
                        }
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
        Hash128 sourceId, string prefix, int bn, int batchSize, int commitEpoch,
        ISubstrateReader? containmentReader = null) =>
        new SubstrateChangeBuilder(sourceId, $"{prefix}/{bn}", null,
            // Compose entities/physicalities drain into the native ContentStage now, not these
            // managed arrays — so capacity is sized for witness rows + PRECEDES, not the old
            // batchSize*32 fanout (the second measured 16 GB blowup co-cause).
            entityCapacity: batchSize,
            physicalityCapacity: batchSize,
            attestationCapacity: batchSize * 4)
            .SetCommitEpoch(commitEpoch)
            // Witness content emissions (ContentWitnessBatch.*) are deferred and tier-deduped per
            // batch when a reader is available; compose entities still drain straight into the stage.
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
            ? await containmentReader.EntitiesExistBitmapAsync(composer.EntityIds(), ct)
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
