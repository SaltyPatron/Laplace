using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

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
        if (records.Count == 0) return [];

        // Root-level gate (cheap ContentRootId) then O(tiers) Merkle existence on
        // every flush batch — working-set mode included. At most five tier rounds
        // per batch regardless of document length; perfcache resolves tier-0 before SPI.
        var shortcircuited = await IngestExistenceGate.RemovePresentAsync(
            records, handler, reader, builder, probedAbsent, ct).ConfigureAwait(false);
        if (records.Count == 0)
            return shortcircuited;

        var units = new IIngestDeferredUnit[records.Count];
        int composeWorkers = Math.Min(
            Math.Max(1, CpuTopology.ResolveCpuBoundWorkers(headroom: 1, maxCap: 8)),
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

        var pending = new List<(TRecord Record, IIngestDeferredUnit Unit)>(records.Count);
        for (int i = 0; i < records.Count; i++)
            pending.Add((records[i], units[i]));
        records.Clear();

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

        byte[]?[]? flatBitmaps = flatTrees.Count > 0
            ? await ContentTierSpine.BatchExistenceEmitBitmapsAsync(
                flatTrees, reader, probedAbsent, ct).ConfigureAwait(false)
            : null;

        var drained = new List<(TRecord, long)>(pending.Count + shortcircuited.Length);
        drained.AddRange(shortcircuited);

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
            drained.Add((record, handler.UnitsPerRecord(record)));
        }
        return drained.ToArray();
    }

    private static byte[]? NormalizeEmitBitmap(byte[]? bm)
    {
        if (bm is null || bm.Length == 0) return null;
        for (int i = 0; i < bm.Length; i++)
            if (bm[i] != 0) return bm;
        return null;
    }
}
