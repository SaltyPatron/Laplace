using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>Compose-only batch accumulated across flush intervals within one working set.</summary>
internal sealed class WorkingSetDeferredBatch<TRecord>
{
    internal readonly List<(TRecord Record, IIngestDeferredUnit Unit)> Pending = new();
    internal readonly List<(TRecord Record, long Units)> Shortcircuited = new();

    internal bool HasWork => Pending.Count > 0 || Shortcircuited.Count > 0;
}

internal static class IngestDescentFlush
{
    internal static Task<(TRecord Record, long Units)[]> ProbeAndDrainAsync<TRecord>(
        List<TRecord> records,
        IIngestRecordHandler<TRecord> handler,
        ISubstrateReader reader,
        SubstrateChangeBuilder builder,
        IngestBatchConfig config,
        CancellationToken ct)
        => ProbeAndDrainAsync(records, handler, reader, builder, config, probedAbsent: null, ct);

    internal static async Task<(TRecord Record, long Units)[]> ProbeAndDrainAsync<TRecord>(
        List<TRecord> records,
        IIngestRecordHandler<TRecord> handler,
        ISubstrateReader reader,
        SubstrateChangeBuilder builder,
        IngestBatchConfig config,
        ISet<Hash128>? probedAbsent,
        CancellationToken ct)
    {
        var batch = await ComposeBatchAsync(records, handler, reader, builder, probedAbsent, ct)
            .ConfigureAwait(false);
        if (batch.Pending.Count == 0)
            return batch.Shortcircuited.ToArray();

        var pendingRecords = batch.Pending.Select(p => p.Record).ToList();
        await FinalizePendingAsync(batch.Pending, handler, reader, builder, config, probedAbsent, ct)
            .ConfigureAwait(false);
        var drained = new List<(TRecord, long)>(batch.Shortcircuited.Count + pendingRecords.Count);
        drained.AddRange(batch.Shortcircuited);
        foreach (var record in pendingRecords)
            drained.Add((record, handler.UnitsPerRecord(record)));
        return drained.ToArray();
    }

    /// <summary>
    /// Working-set mode: root gate + parallel compose only — no O(tiers) descent until
    /// <see cref="FinalizeWorkingSetAsync"/> (Rule #8 step 5 once per working set).
    /// </summary>
    internal static async Task<WorkingSetDeferredBatch<TRecord>> ComposeBatchAsync<TRecord>(
        List<TRecord> records,
        IIngestRecordHandler<TRecord> handler,
        ISubstrateReader reader,
        SubstrateChangeBuilder builder,
        ISet<Hash128>? probedAbsent,
        CancellationToken ct)
    {
        var batch = new WorkingSetDeferredBatch<TRecord>();
        if (records.Count == 0) return batch;

        var shortcircuited = await IngestExistenceGate.RemovePresentAsync(
            records, handler, reader, builder, probedAbsent, ct).ConfigureAwait(false);
        batch.Shortcircuited.AddRange(shortcircuited);
        if (records.Count == 0) return batch;

        var units = new IIngestDeferredUnit[records.Count];
        int composeWorkers = Math.Min(
            Math.Max(1, IngestTopology.Current.ComposeWorkers),
            records.Count);
        if (composeWorkers <= 1)
        {
            for (int i = 0; i < records.Count; i++)
                units[i] = handler.CreateDeferredUnit(records[i]);
        }
        else
        {
            var snapshot = records;
            int nextIdx = -1;
            await CpuTopology.RunPinnedAsyncParallel(composeWorkers, (_, _) =>
            {
                for (int i = Interlocked.Increment(ref nextIdx); i < snapshot.Count;
                     i = Interlocked.Increment(ref nextIdx))
                    units[i] = handler.CreateDeferredUnit(snapshot[i]);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
        }

        for (int i = 0; i < records.Count; i++)
            batch.Pending.Add((records[i], units[i]));
        records.Clear();
        return batch;
    }

    internal static async Task FinalizeWorkingSetAsync<TRecord>(
        WorkingSetDeferredBatch<TRecord> batch,
        IIngestRecordHandler<TRecord> handler,
        ISubstrateReader reader,
        SubstrateChangeBuilder builder,
        IngestBatchConfig config,
        ISet<Hash128>? probedAbsent,
        CancellationToken ct)
    {
        if (batch.Pending.Count == 0) return;
        await FinalizePendingAsync(batch.Pending, handler, reader, builder, config, probedAbsent, ct)
            .ConfigureAwait(false);
    }

    private static async Task FinalizePendingAsync<TRecord>(
        List<(TRecord Record, IIngestDeferredUnit Unit)> pending,
        IIngestRecordHandler<TRecord> handler,
        ISubstrateReader reader,
        SubstrateChangeBuilder builder,
        IngestBatchConfig config,
        ISet<Hash128>? probedAbsent,
        CancellationToken ct)
    {
        if (pending.Count == 0) return;

        var flatTrees = new List<TierTree?>();
        var treeRanges = new List<(int Start, int Count)>(pending.Count);
        foreach (var (_, unit) in pending)
        {
            if (unit is IMultiTreeIngestDeferredUnit multi)
            {
                var trees = multi.AllProbeTrees;
                treeRanges.Add((flatTrees.Count, trees.Count));
                flatTrees.AddRange(trees);
            }
            else
            {
                treeRanges.Add((flatTrees.Count, 1));
                flatTrees.Add(unit.TreeForBatchProbe);
            }
        }

        // Rule #8 step 5: one O(tiers) cross-working-set existence probe — distinct ids
        // deduped per tier round inside TierTreeDescent (06 L93-94a).
        byte[]?[]? flatBitmaps = await BulkDescent.ProbeFlushBatchAsync(
            flatTrees, reader, probedAbsent, ct).ConfigureAwait(false);

        for (int i = 0; i < pending.Count; i++)
        {
            var (record, unit) = pending[i];
            try
            {
                Hash128 root;
                if (unit is IMultiTreeIngestDeferredUnit multi)
                {
                    var (start, count) = treeRanges[i];
                    root = flatBitmaps is null
                        ? multi.DrainInto(builder, config.WitnessWeight, ReadOnlySpan<byte[]?>.Empty)
                        : multi.DrainInto(builder, config.WitnessWeight, flatBitmaps.AsSpan(start, count));
                }
                else
                {
                    byte[]? bm = flatBitmaps is null
                        ? null
                        : NormalizeEmitBitmap(flatBitmaps[treeRanges[i].Start]);
                    root = unit.DrainInto(builder, config.WitnessWeight, bm);
                }
                handler.WalkWitness(record, root, builder, unit);
                if (root != default)
                    reader.MarkProven([root]);
            }
            finally
            {
                unit.Dispose();
            }
        }
        pending.Clear();
    }

    private static byte[]? NormalizeEmitBitmap(byte[]? bm)
    {
        if (bm is null || bm.Length == 0) return null;
        for (int i = 0; i < bm.Length; i++)
            if (bm[i] != 0) return bm;
        return null;
    }
}

/// <summary>
/// P5 cross-batch O(tiers) bulk descent: one
/// <see cref="ContentTierSpine.BatchExistenceEmitBitmapsAsync"/> call per flush
/// batch regardless of record count — at most five tier rounds total.
/// </summary>
internal static class BulkDescent
{
    internal static Task<byte[]?[]> ProbeFlushBatchAsync(
        IReadOnlyList<TierTree?> flatTrees,
        ISubstrateReader reader,
        ISet<Hash128>? probedAbsent,
        CancellationToken ct)
    {
        if (flatTrees.Count == 0)
            return Task.FromResult(Array.Empty<byte[]?>());
        return ContentTierSpine.BatchExistenceEmitBitmapsAsync(
            flatTrees, reader, probedAbsent, ct);
    }
}
