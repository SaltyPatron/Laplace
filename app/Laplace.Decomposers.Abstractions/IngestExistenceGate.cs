using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

internal static class IngestExistenceGate
{
    internal static Task<(TRecord Record, long Units)[]> RemovePresentAsync<TRecord>(
        List<TRecord> records,
        IIngestRecordHandler<TRecord> handler,
        ISubstrateReader reader,
        SubstrateChangeBuilder builder,
        CancellationToken ct)
        => RemovePresentAsync(records, handler, reader, builder, probedAbsent: null, ct);

    internal static async Task<(TRecord Record, long Units)[]> RemovePresentAsync<TRecord>(
        List<TRecord> records,
        IIngestRecordHandler<TRecord> handler,
        ISubstrateReader reader,
        SubstrateChangeBuilder builder,
        ISet<Hash128>? probedAbsent,
        CancellationToken ct)
    {
        if (records.Count == 0) return [];

        var shortcircuited = new List<(TRecord, long)>();
        var roots = new List<(int Index, Hash128 RootId)>();
        var rootIndex = new int[records.Count];
        Array.Fill(rootIndex, -1);

        for (int i = 0; i < records.Count; i++)
        {
            if (!TryResolveRoot(records[i], handler, out var rootId)) continue;

            if (reader.IsProvenPresent(rootId))
            {
                ApplyWitness(records[i], rootId, handler, builder);
                reader.MarkProven([rootId]);
                shortcircuited.Add((records[i], handler.UnitsPerRecord(records[i])));
                rootIndex[i] = -2;
                continue;
            }

            // Root already probed absent within this working set: it cannot
            // have appeared since our own unwritten working set began, so
            // skip the re-probe and let the record flow through the normal
            // deferred-unit path (stage witness-dedup absorbs re-emission,
            // WalkWitness still counts the observation).
            if (probedAbsent is not null && probedAbsent.Contains(rootId)) continue;

            rootIndex[i] = roots.Count;
            roots.Add((i, rootId));
        }

        if (roots.Count > 0)
        {
            var ids = new Hash128[roots.Count];
            for (int k = 0; k < roots.Count; k++) ids[k] = roots[k].RootId;
            byte[] bm = await reader.EntitiesExistBitmapAsync(ids, ct).ConfigureAwait(false);
            long bits = (long)bm.Length * 8;
            for (int k = 0; k < roots.Count; k++)
            {
                bool present = k < bits && (bm[k >> 3] & (1 << (k & 7))) != 0;
                if (!present)
                {
                    probedAbsent?.Add(roots[k].RootId);
                    continue;
                }
                int i = roots[k].Index;
                ApplyWitness(records[i], roots[k].RootId, handler, builder);
                reader.MarkProven([roots[k].RootId]);
                shortcircuited.Add((records[i], handler.UnitsPerRecord(records[i])));
                rootIndex[i] = -2;
            }
        }

        var novel = new List<TRecord>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            if (rootIndex[i] == -2) continue;
            novel.Add(records[i]);
        }
        records.Clear();
        records.AddRange(novel);
        return shortcircuited.ToArray();
    }

    private static bool TryResolveRoot<TRecord>(
        TRecord record, IIngestRecordHandler<TRecord> handler, out Hash128 rootId)
    {
        rootId = default;
        if (record is GrammarIngestRecord gr && handler is GrammarIngestHandler grammar)
            return GrammarRowComposer.TryProbeRowRoot(
                gr.LineUtf8, gr.Ast, grammar.ModalityId, out rootId, out _);
        if (record is ContentIngestRecord cr && handler is ContentIngestHandler or DocumentIngestHandler)
        {
            Hash128? id = TextDecomposer.ContentRootId(cr.CanonicalUtf8);
            if (id is null) return false;
            rootId = id.Value;
            return true;
        }
        return false;
    }

    private static void ApplyWitness<TRecord>(
        TRecord record, Hash128 rootId, IIngestRecordHandler<TRecord> handler, SubstrateChangeBuilder builder)
    {
        if (handler is GrammarIngestHandler grammar && record is GrammarIngestRecord gr)
            grammar.WalkWitnessWithoutCompose(gr, rootId, builder);
        else if (rootId != default)
            handler.WalkWitness(record, rootId, builder, PresentRootDeferredUnit.Instance);
    }

    private sealed class PresentRootDeferredUnit : IIngestDeferredUnit
    {
        internal static readonly PresentRootDeferredUnit Instance = new();
        public TierTree? TreeForBatchProbe => null;
        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            Task.FromResult<byte[]?>(null);
        public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap) =>
            default;
        public void Dispose() { }
    }
}
