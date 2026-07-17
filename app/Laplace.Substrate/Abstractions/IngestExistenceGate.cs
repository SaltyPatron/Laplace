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

        // Relation triples carry TWO content roots (subject + object); the single-root
        // machinery below cannot gate them, so they get a dedicated both-roots pass.
        if (records is List<RelationTripleRecord> triples && handler is RelationTripleHandler tripleHandler)
        {
            var sc = await RemovePresentTriplesAsync(
                triples, tripleHandler, reader, builder, probedAbsent, ct).ConfigureAwait(false);
            return ((TRecord Record, long Units)[])(object)sc;
        }

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

    // Two-root existence gate for relation triples: a record short-circuits (skips BOTH
    // tier-tree composes in CreateDeferredUnit) only when subject AND object phrases are
    // proven present; its testimony — the edge plus POS/synset/language facts — is still
    // emitted for every short-circuited record (testimony is per-record, never deduped).
    // Root ids come from the same native ContentRootId the ContentIngestRecord branch
    // trusts; identity is exact, so a resolved root equals the composed tree's root.
    private static async Task<(RelationTripleRecord Record, long Units)[]> RemovePresentTriplesAsync(
        List<RelationTripleRecord> records,
        RelationTripleHandler handler,
        ISubstrateReader reader,
        SubstrateChangeBuilder builder,
        ISet<Hash128>? probedAbsent,
        CancellationToken ct)
    {
        IIngestRecordHandler<RelationTripleRecord> h = handler;
        var shortcircuited = new List<(RelationTripleRecord, long)>();
        var roots = new (Hash128 Subject, Hash128 Object)[records.Count];
        var removed = new bool[records.Count];

        // Distinct unproven roots probed once per batch (phrases repeat heavily in triples).
        var probeIds = new List<Hash128>();
        var probeSlot = new Dictionary<Hash128, int>();
        var candidates = new List<(int Index, int SubjectSlot, int ObjectSlot)>();

        int Slot(Hash128 root)
        {
            if (!probeSlot.TryGetValue(root, out int s))
            {
                s = probeIds.Count;
                probeIds.Add(root);
                probeSlot[root] = s;
            }
            return s;
        }

        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (r.SubjectCanonical is not { Length: > 0 } subj
                || r.ObjectCanonical is not { Length: > 0 } obj) continue;
            Hash128 sRoot, oRoot;
            try
            {
                if (TextDecomposer.ContentRootId(subj) is not { } s0
                    || TextDecomposer.ContentRootId(obj) is not { } o0) continue;
                (sRoot, oRoot) = (s0, o0);
            }
            // Malformed phrase: fall through to the deferred-unit path, whose TryBuild
            // logs and skips it exactly as before this gate existed.
            catch (InvalidOperationException) { continue; }
            roots[i] = (sRoot, oRoot);

            bool sProven = reader.IsProvenPresent(sRoot);
            bool oProven = reader.IsProvenPresent(oRoot);
            if (sProven && oProven)
            {
                handler.WitnessPresentPair(in r, sRoot, oRoot, builder);
                shortcircuited.Add((r, h.UnitsPerRecord(r)));
                removed[i] = true;
                continue;
            }

            // A root already probed absent within this working set cannot have appeared
            // since our own unwritten working set began — compose normally, no re-probe.
            if (probedAbsent is not null
                && ((!sProven && probedAbsent.Contains(sRoot))
                    || (!oProven && probedAbsent.Contains(oRoot))))
                continue;

            candidates.Add((i, sProven ? -1 : Slot(sRoot), oProven ? -1 : Slot(oRoot)));
        }

        if (probeIds.Count > 0)
        {
            byte[] bm = await reader.EntitiesExistBitmapAsync(probeIds, ct).ConfigureAwait(false);
            long bits = (long)bm.Length * 8;
            bool Present(int slot) => slot < bits && (bm[slot >> 3] & (1 << (slot & 7))) != 0;

            var proven = new List<Hash128>();
            for (int s = 0; s < probeIds.Count; s++)
            {
                if (Present(s)) proven.Add(probeIds[s]);
                else probedAbsent?.Add(probeIds[s]);
            }
            if (proven.Count > 0) reader.MarkProven(proven);

            foreach (var (i, sSlot, oSlot) in candidates)
            {
                if ((sSlot >= 0 && !Present(sSlot)) || (oSlot >= 0 && !Present(oSlot))) continue;
                var r = records[i];
                handler.WitnessPresentPair(in r, roots[i].Subject, roots[i].Object, builder);
                shortcircuited.Add((r, h.UnitsPerRecord(r)));
                removed[i] = true;
            }
        }

        var novel = new List<RelationTripleRecord>(records.Count);
        for (int i = 0; i < records.Count; i++)
            if (!removed[i]) novel.Add(records[i]);
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
        if (record is ITrunkRootRecord trunk)
        {
            rootId = trunk.TrunkRootId;
            return rootId != default;
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
