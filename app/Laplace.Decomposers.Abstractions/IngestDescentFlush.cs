using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

internal static class IngestDescentFlush
{
    internal static async Task<(TRecord Record, long Units)[]> ProbeAndDrainAsync<TRecord>(
        List<TRecord> records,
        IIngestRecordHandler<TRecord> handler,
        ISubstrateReader reader,
        SubstrateChangeBuilder builder,
        IngestBatchConfig config,
        CancellationToken ct)
    {
        if (records.Count == 0) return [];

        var shortcircuited = await IngestExistenceGate.RemovePresentAsync(
            records, handler, reader, builder, ct).ConfigureAwait(false);

        if (records.Count == 0)
            return shortcircuited;

        var pending = new List<(TRecord Record, IIngestDeferredUnit Unit)>(records.Count);
        foreach (var record in records)
            pending.Add((record, handler.CreateDeferredUnit(record)));
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

        byte[]?[] flatBitmaps = await TierTreeContainmentProbe
            .ProbeBatchNodeEmitBitmapsAsync(flatTrees, reader, ct).ConfigureAwait(false);

        var drained = new List<(TRecord, long)>(pending.Count + shortcircuited.Length);
        drained.AddRange(shortcircuited);

        for (int i = 0; i < pending.Count; i++)
        {
            var (record, unit) = pending[i];
            var (start, count) = treeRanges[i];
            try
            {
                Hash128 root = unit is IMultiTreeIngestDeferredUnit multi
                    ? multi.DrainInto(builder, config.WitnessWeight, flatBitmaps.AsSpan(start, count))
                    : unit.DrainInto(builder, config.WitnessWeight,
                        NormalizeEmitBitmap(flatBitmaps[start]));
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
