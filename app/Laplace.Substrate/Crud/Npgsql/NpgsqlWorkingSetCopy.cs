using System.Runtime.InteropServices;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD.Npgsql;

public sealed partial class NpgsqlSubstrateWriter
{
    /// <summary>
    /// 16-octet memcmp key matching Postgres bytea/btree order for the column
    /// that partitions parallel COPY groups. For entities/attestations that is
    /// the row id (<see cref="Hash128"/>); for physicalities it is the 128-bit
    /// hilbert curve index. Storage here is wire order only — not a claim that
    /// hilbert is a hash.
    /// </summary>
    private readonly struct CopyPartitionKey
    {
        private readonly Hash128 _wire;
        private CopyPartitionKey(Hash128 wire) => _wire = wire;
        public Hash128 Wire => _wire;
        public static CopyPartitionKey ForEntityId(Hash128 id) => new(id);
        public static CopyPartitionKey ForHilbertIndex(Hilbert128 index)
        {
            Span<byte> pack = stackalloc byte[16];
            index.WriteBytes(pack);
            return new(Hash128.FromBytes(pack));
        }
        public int CompareToBytewise(CopyPartitionKey other) =>
            _wire.CompareToBytewise(other._wire);
    }

    /// <summary>Pre-reversed 128-bit key for Array.Sort (memcmp order, no per-compare work).</summary>
    private readonly record struct CopySortKey(ulong HiBe, ulong LoBe) : IComparable<CopySortKey>
    {
        public static CopySortKey FromWire(Hash128 wire) => new(
            System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(wire.Hi),
            System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(wire.Lo));
        public int CompareTo(CopySortKey other)
        {
            int c = HiBe.CompareTo(other.HiBe);
            return c != 0 ? c : LoBe.CompareTo(other.LoBe);
        }
    }

    /// <summary>
    /// <para><b>RouteKey</b> is the value of the table's HASH partition column (the id for
    /// entities and physicalities, the subject for attestations). It names the leaf that
    /// owns the row, and each leaf rides exactly one COPY connection.</para>
    ///
    /// <para><b>OrderKey</b> is the sort within a leaf, which decides the order index pages
    /// are touched: the id for the btree tables, the hilbert index for physicalities,
    /// whose contended index is the coord GiST.</para>
    ///
    /// <para>Patch/PatchSum carry a duplicate-collapsed group's summed games/sum_score for
    /// the representative row (Patch = -1 means unpatched).</para>
    /// </summary>
    private readonly record struct KeptRow(
        Hash128 RouteKey, CopyPartitionKey OrderKey, StagedRowRef Row,
        long Patch, int CountOff, long PatchSum = 0, int SumOff = 0);

    /// <summary>
    /// CLR array-addressability ceiling for the one contiguous payload PackFiltered
    /// builds. Measured in bytes, never rows: a single row can carry a very large
    /// trajectory.
    /// </summary>
    private static readonly long MaxCopyPayloadBytes = Array.MaxLength;

    /// <summary>
    /// Splits a kept range into byte-bounded COPY payloads. PackFiltered packs one call
    /// into a single byte[] and refuses past the array ceiling, so chunks are bounded by
    /// bytes: a few rows can carry gigabytes of trajectory. Each chunk gets its own COPY
    /// stream through CopyFilteredAsync; the wire protocol is unchanged.
    /// </summary>
    private static async Task CopyKeptAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, IReadOnlyList<KeptRow> kept,
        int start, int count, CancellationToken ct)
    {
        int end = start + count;
        for (int i = start; i < end;)
        {
            long bytes = 0;
            int chunk = 0;
            while (i + chunk < end)
            {
                long len = kept[i + chunk].Row.Length;
                // Always take at least one row. A single row over the ceiling cannot
                // be split here; it must reach PackFiltered and fail with that
                // function's explicit size message rather than spin taking zero rows.
                if (chunk > 0 && bytes + len > MaxCopyPayloadBytes) break;
                bytes += len;
                chunk++;
            }
            await CopyKeptChunkAsync(conn, tableName, table, blobs, kept, i, chunk, ct);
            i += chunk;
        }
    }

    private static async Task CopyKeptChunkAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, IReadOnlyList<KeptRow> kept,
        int start, int count, CancellationToken ct)
    {
        var rows = new List<StagedRowRef>(count);
        long[]? patches = null;
        int[]? countOffs = null;
        long[]? sumPatches = null;
        int[]? sumOffs = null;
        bool anyPatch = false;
        for (int i = start; i < start + count; i++)
            if (kept[i].Patch >= 0) { anyPatch = true; break; }
        if (anyPatch)
        {
            patches = new long[count];
            countOffs = new int[count];
            sumPatches = new long[count];
            sumOffs = new int[count];
        }
        for (int i = 0; i < count; i++)
        {
            var k = kept[start + i];
            rows.Add(k.Row);
            if (patches is not null)
            {
                patches[i] = k.Patch;
                countOffs![i] = k.CountOff;
                sumPatches![i] = k.PatchSum;
                sumOffs![i] = k.SumOff;
            }
        }
        await CopyFilteredAsync(conn, tableName, table, blobs, rows, patches, countOffs, sumPatches, sumOffs, ct);
    }

    private static int ResolveCopyGroups(int rowCount, long payloadBytes, int workerLimit = int.MaxValue)
    {
        // Each group owns whole leaves (RouteLeaves). Fanout is
        // justified by finalized payload bytes: another connection must receive at
        // least one transport buffer/page and one row. No row-count crossover or
        // host-specific worker cap survives here; topology owns the ceiling.
        int maxWorkers = Math.Max(1, Math.Min(ApplyParallelism, workerLimit));
        return IngestSizing.ResolveCopyConnections(
            rowCount, payloadBytes, maxWorkers, ApplySizing.CopyStartupBytes);
    }

    private static long TotalEntityBytes(
        CopyTupleParser.EntityRows entities, IReadOnlyList<int> indices)
    {
        long bytes = 0;
        for (int i = 0; i < indices.Count; i++)
            bytes = checked(bytes + entities.Rows[indices[i]].Length);
        return bytes;
    }

    private static long TotalKeptBytes(IReadOnlyList<KeptRow> kept)
    {
        long bytes = 0;
        for (int i = 0; i < kept.Count; i++)
            bytes = checked(bytes + kept[i].Row.Length);
        return bytes;
    }

    /// <summary>
    /// Assign whole leaves to COPY lanes: largest leaf first to the least-loaded lane.
    /// Each backend then writes its own leaves, their rows contiguous, and no two
    /// backends insert into the same btree.
    /// </summary>
    internal static int[] LaneOfLeaf(ReadOnlySpan<int> rowsPerLeaf, int groups)
    {
        var lanes = new int[rowsPerLeaf.Length];
        if (groups <= 1) return lanes;
        var order = new int[rowsPerLeaf.Length];
        var sizes = new int[rowsPerLeaf.Length];
        for (int leaf = 0; leaf < order.Length; leaf++)
        {
            order[leaf] = leaf;
            sizes[leaf] = -rowsPerLeaf[leaf];
        }
        Array.Sort(sizes, order);
        var load = new long[groups];
        foreach (int leaf in order)
        {
            int lane = 0;
            for (int g = 1; g < groups; g++)
                if (load[g] < load[lane]) lane = g;
            lanes[leaf] = lane;
            load[lane] += rowsPerLeaf[leaf];
        }
        return lanes;
    }

    /// <summary>Owning leaf of each route key and the lane of each row.</summary>
    private static (int[] Leaf, int[] Lane, int[] Counts) RouteLeaves(
        ReadOnlySpan<Hash128> routeKeys, int modulus, int groups)
    {
        var leaf = new int[routeKeys.Length];
        PartitionRoute.Remainders(routeKeys, modulus, leaf);
        var rowsPerLeaf = new int[modulus];
        foreach (int l in leaf) rowsPerLeaf[l]++;
        int[] laneOfLeaf = LaneOfLeaf(rowsPerLeaf, groups);
        var lane = new int[leaf.Length];
        var counts = new int[Math.Max(1, groups)];
        for (int i = 0; i < leaf.Length; i++)
        {
            lane[i] = laneOfLeaf[leaf[i]];
            counts[lane[i]]++;
        }
        return (leaf, lane, counts);
    }

    /// <summary>Leaf first, then the order key within the leaf.</summary>
    private readonly record struct LeafOrderKey(int Leaf, CopySortKey Order) : IComparable<LeafOrderKey>
    {
        public int CompareTo(LeafOrderKey other)
        {
            int c = Leaf.CompareTo(other.Leaf);
            return c != 0 ? c : Order.CompareTo(other.Order);
        }
    }

    private static byte[][] BuildSortedEntityPayloads(
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs,
        CopyTupleParser.EntityRows ents, List<int> firstIdx, int groups, int modulus,
        out int[] rowsByLane)
    {
        int rowCount = firstIdx.Count;
        var routes = new Hash128[rowCount];
        for (int k = 0; k < rowCount; k++) routes[k] = ents.Ids[firstIdx[k]];
        var (leafOf, groupOf, counts) = RouteLeaves(routes, modulus, groups);
        var keysAll = new LeafOrderKey[rowCount];
        for (int k = 0; k < rowCount; k++)
            keysAll[k] = new LeafOrderKey(leafOf[k], CopySortKey.FromWire(routes[k]));
        var groupRefs = new StagedRowRef[groups][];
        var groupKeys = new LeafOrderKey[groups][];
        var next = new int[groups];
        for (int g = 0; g < groups; g++)
        {
            groupRefs[g] = new StagedRowRef[counts[g]];
            groupKeys[g] = new LeafOrderKey[counts[g]];
        }
        for (int k = 0; k < rowCount; k++)
        {
            int g = groupOf[k];
            int o = next[g]++;
            groupRefs[g][o] = ents.Rows[firstIdx[k]];
            groupKeys[g][o] = keysAll[k];
        }
        var payloads = new byte[groups][];
        Parallel.For(0, groups, g =>
        {
            Array.Sort(groupKeys[g], groupRefs[g]);
            payloads[g] = groupRefs[g].Length == 0
                ? Array.Empty<byte>()
                : CopyTupleParser.PackFiltered(blobs, groupRefs[g]);
        });
        rowsByLane = counts;
        return payloads;
    }

    private static void SortKeptByLeafAndOrder(List<KeptRow> kept, int modulus)
    {
        int rowCount = kept.Count;
        if (rowCount <= 1 || modulus <= 1) return;
        var span = CollectionsMarshal.AsSpan(kept);
        var routes = new Hash128[rowCount];
        for (int i = 0; i < rowCount; i++) routes[i] = span[i].RouteKey;
        var leafOf = new int[rowCount];
        PartitionRoute.Remainders(routes, modulus, leafOf);
        var keys = new LeafOrderKey[rowCount];
        for (int i = 0; i < rowCount; i++)
            keys[i] = new LeafOrderKey(leafOf[i], CopySortKey.FromWire(span[i].OrderKey.Wire));
        MemoryExtensions.Sort(keys.AsSpan(), span);
    }

    private static byte[][] BuildSortedCopyPayloads(
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept, int groups, int modulus,
        out int[] rowsByLane)
    {
        int rowCount = kept.Count;
        var routes = new Hash128[rowCount];
        for (int i = 0; i < rowCount; i++) routes[i] = kept[i].RouteKey;
        var (leafOf, groupOf, counts) = RouteLeaves(routes, modulus, groups);
        var keysAll = new LeafOrderKey[rowCount];
        for (int i = 0; i < rowCount; i++)
            keysAll[i] = new LeafOrderKey(leafOf[i], CopySortKey.FromWire(kept[i].OrderKey.Wire));
        var groupRows = new KeptRow[groups][];
        var groupKeys = new LeafOrderKey[groups][];
        var next = new int[groups];
        for (int g = 0; g < groups; g++)
        {
            groupRows[g] = new KeptRow[counts[g]];
            groupKeys[g] = new LeafOrderKey[counts[g]];
        }
        for (int i = 0; i < rowCount; i++)
        {
            int g = groupOf[i];
            int o = next[g]++;
            groupRows[g][o] = kept[i];
            groupKeys[g][o] = keysAll[i];
        }
        var payloads = new byte[groups][];
        Parallel.For(0, groups, g =>
        {
            Array.Sort(groupKeys[g], groupRows[g]);
            var rows = groupRows[g];
            if (rows.Length == 0) { payloads[g] = Array.Empty<byte>(); return; }
            var refs = new StagedRowRef[rows.Length];
            for (int i = 0; i < rows.Length; i++) refs[i] = rows[i].Row;
            long[]? patches = null;
            int[]? countOffs = null;
            long[]? sumPatches = null;
            int[]? sumOffs = null;
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].Patch < 0) continue;
                patches = new long[rows.Length];
                countOffs = new int[rows.Length];
                sumPatches = new long[rows.Length];
                sumOffs = new int[rows.Length];
                Array.Fill(patches, -1);
                for (int j = 0; j < rows.Length; j++)
                {
                    patches[j] = rows[j].Patch;
                    countOffs[j] = rows[j].CountOff;
                    sumPatches[j] = rows[j].PatchSum;
                    sumOffs[j] = rows[j].SumOff;
                }
                break;
            }
            payloads[g] = CopyTupleParser.PackFiltered(
                blobs, refs, patches, countOffs, sumPatches, sumOffs);
        });
        rowsByLane = counts;
        return payloads;
    }

    private async Task<int> CopyPayloadsParallelAsync(
        string tableName, IntentStageTable table,
        int rowCount, int groups, byte[][] payloads, int[] rowsByLane, long sortMs,
        CopyTransactionCounts copyTransactions, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new Task[groups];
        // One value per COPY lane, including pool/semaphore wait and commit.
        // Reuse packing counts; do not parse the row payload again for diagnostics.
        var elapsedMsByLane = new double[groups];
        for (int g = 0; g < groups; g++)
        {
            int group = g;
            tasks[g] = Task.Run(async () =>
            {
                var payload = payloads[group];
                if (payload.Length == 0) return;
                long laneStarted = System.Diagnostics.Stopwatch.GetTimestamp();

                // Claim a slot in the plan's COPY share before renting from the pool:
                // overlapping phases share one budget, so a group waits on a cancellable
                // semaphore instead of timing out on a pool rent and failing the batch.
                using (var waitDiagnostic = MeasureApplyPhase(
                    "copy-lane-wait", tableName, lane: group, rows: rowsByLane[group]))
                {
                    await CopyConnectionBudget.WaitAsync(ct).ConfigureAwait(false);
                    waitDiagnostic?.Complete();
                }
                try
                {
                using var setupDiagnostic = MeasureApplyPhase(
                    "copy-lane-setup", tableName, lane: group, rows: rowsByLane[group]);
                await using var conn = await _ds.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                Interlocked.Increment(ref copyTransactions.Started);
                // The epoch advance rides the GUC batch in the same round trip; the
                // command has no positional parameters (Npgsql forbids $n in
                // multi-statement commands). nextval runs before the COPY below; each
                // parallel sub-transaction is its own write transaction and advances
                // once. The route flag was resolved on the control connection first.
                bool epochBump = Volatile.Read(ref _applyWriteEpochRoute) == 1;
                string settings = TransactionSettingsQuery(Durability);
                await using (var guc = NpgsqlCatalog.Command(conn, tx,
                    epochBump ? [settings, "write.advance_epoch"] : [settings]))
                {
                    await guc.ExecuteNonQueryAsync(ct);
                }
                if (epochBump) Interlocked.Increment(ref _epochOwnBumpsSinceBaseline);
                var stream = await conn.BeginRawBinaryCopyAsync(
                    NpgsqlCatalog.BinaryCopyStatement(table), ct);
                // Disposing the stream completes the COPY and reads its acknowledgement,
                // even when writing throws; the transaction and budget slot are released
                // by their own enclosing scopes.
                try
                {
                    setupDiagnostic?.Complete();
                    using var writeDiagnostic = MeasureApplyPhase(
                        "copy-lane-write", tableName, lane: group, rows: rowsByLane[group]);
                    await CopyTupleParser.WritePackedAsync(stream, payload, ct);
                    writeDiagnostic?.Complete();
                }
                finally
                {
                    using var finishDiagnostic = MeasureApplyPhase(
                        "copy-lane-finish", tableName, lane: group, rows: rowsByLane[group]);
                    await stream.DisposeAsync();
                    finishDiagnostic?.Complete();
                }
                using (var commitDiagnostic = MeasureApplyPhase(
                    "copy-lane-commit", tableName, lane: group, rows: rowsByLane[group]))
                {
                    await tx.CommitAsync(ct);
                    commitDiagnostic?.Complete();
                }
                Interlocked.Increment(ref copyTransactions.Committed);
                }
                finally
                {
                    // The connection and transaction are disposed on the way out of the
                    // try block, so the slot is released only after the pool has the
                    // connection back.
                    CopyConnectionBudget.Release();
                    elapsedMsByLane[group] =
                        System.Diagnostics.Stopwatch.GetElapsedTime(laneStarted).TotalMilliseconds;
                }
            }, ct);
        }
        await Task.WhenAll(tasks);
        sw.Stop();
        _log.LogInformation(
            "WS_APPLY copy {Table}: {Rows:N0} rows across {Groups} id-range connection(s) in {Ms:N0}ms ({Rps:N0} rows/s; sort {SortMs:N0}ms; lane rows {RowsByLane}; lane bytes {BytesByLane}; lane elapsed_ms {ElapsedMsByLane})",
            tableName, rowCount, groups, sw.ElapsedMilliseconds,
            rowCount / Math.Max(1e-3, sw.Elapsed.TotalSeconds), sortMs,
            string.Join(",", rowsByLane),
            string.Join(",", payloads.Select(static payload => payload.LongLength)),
            string.Join(",", elapsedMsByLane.Select(static value =>
                value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
        return 1;
    }

    private async Task<int> CopyPhaseParallelAsync(
        string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, List<KeptRow> kept,
        CopyTransactionCounts copyTransactions, CancellationToken ct)
    {
        if (kept.Count == 0) return 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long payloadBytes = TotalKeptBytes(kept);
        int groups = ResolveCopyGroups(kept.Count, payloadBytes, ResolveWorkerConnectionLimit(_ds));
        // Bounds-check StagedRowRef.Blob before packing: an IndexOutOfRange inside
        // BuildSortedCopyPayloads / PackFiltered carries no table name on the stack
        // (Release builds inline the packer), so the check names it here.
        int blobCount = blobs.Count;
        int minBlob = int.MaxValue, maxBlob = int.MinValue;
        for (int i = 0; i < kept.Count; i++)
        {
            int b = kept[i].Row.Blob;
            if (b < minBlob) minBlob = b;
            if (b > maxBlob) maxBlob = b;
            if ((uint)b >= (uint)blobCount)
                throw new InvalidOperationException(
                    $"COPY pack {tableName}: kept[{i}].Row.Blob={b} outside blobs[0..{blobCount}) "
                    + $"(kept={kept.Count:N0}, groups={groups}, patch={kept[i].Patch}, "
                    + $"len={kept[i].Row.Length}, off={kept[i].Row.Offset})");
        }
        byte[][] payloads;
        int[] rowsByLane;
        try
        {
            payloads = BuildSortedCopyPayloads(blobs, kept, groups, _leafModulus, out rowsByLane);
        }
        catch (IndexOutOfRangeException ex)
        {
            throw new InvalidOperationException(
                $"COPY pack {tableName}: IndexOutOfRange in BuildSortedCopyPayloads "
                + $"(kept={kept.Count:N0}, groups={groups}, blobs={blobCount}, "
                + $"blobIndexRange=[{minBlob}..{maxBlob}], payloadBytes={payloadBytes})",
                ex);
        }
        long sortMs = sw.ElapsedMilliseconds;
        return await CopyPayloadsParallelAsync(
            tableName, table, kept.Count, groups, payloads, rowsByLane, sortMs, copyTransactions, ct);
    }

    private static async Task CopyFilteredAsync(
        NpgsqlConnection conn, string tableName, IntentStageTable table,
        IReadOnlyList<(IntPtr Ptr, long Len)> blobs, IReadOnlyList<StagedRowRef> rows,
        long[]? patchedCounts, IReadOnlyList<int>? countValueOffsets,
        long[]? patchedSums, IReadOnlyList<int>? sumValueOffsets, CancellationToken ct)
    {
        // Pack before opening the stream. PackFiltered can throw (its payload size
        // ceiling is the usual case); with the COPY already open, disposing the stream
        // would make the server report 22P04 "COPY file signature not recognized",
        // replacing the real exception. Packing first lets the real error propagate.
        byte[] packed = CopyTupleParser.PackFiltered(
            blobs, rows, patchedCounts, countValueOffsets, patchedSums, sumValueOffsets);
        await using var stream = await conn.BeginRawBinaryCopyAsync(
            NpgsqlCatalog.BinaryCopyStatement(table), ct);
        await CopyTupleParser.WritePackedAsync(stream, packed, ct);
    }
}
