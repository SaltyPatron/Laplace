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

        if (records is List<RelationTripleRecord> triples && handler is RelationTripleHandler tripleHandler)
        {
            var sc = await RemovePresentTriplesAsync(
                triples, tripleHandler, reader, builder, probedAbsent, ct).ConfigureAwait(false);
            return ((TRecord Record, long Units)[])(object)sc;
        }

        var shortcircuited = new List<(TRecord, long)>();
        var perFile = handler as DocumentIngestHandler;
        var roots = new List<(int Index, Hash128 RootId)>();
        var presentFileRoots = new List<(int Index, Hash128 CompletionId)>();
        var rootIndex = new int[records.Count];
        Array.Fill(rootIndex, -1);

        static Hash128 CompletionIdFor(TRecord record, Hash128 contentRoot)
            => record is ContentIngestRecord cr && cr.FileId != default
                ? cr.FileId
                : contentRoot;

        // Content presence and file completion are different identities. The content root
        // answers whether the shared DAG already exists. The file-composition id answers
        // whether THIS occurrence (content + identity metadata) already completed.
        for (int i = 0; i < records.Count; i++)
        {
            if (!TryResolveRoot(records[i], handler, out var rootId, out var unresolvable))
            {
                if (unresolvable) rootIndex[i] = -2;
                continue;
            }

            if (reader.IsProvenPresent(rootId))
            {
                if (perFile is not null)
                {
                    if (!perFile.IgnoreCompletedFiles)
                        presentFileRoots.Add((i, CompletionIdFor(records[i], rootId)));
                    continue;
                }
                ApplyWitness(records[i], rootId, handler, builder);
                reader.MarkProven([rootId]);
                shortcircuited.Add((records[i], handler.UnitsPerRecord(records[i])));
                ReleaseNativeArtifacts(records[i], handler);
                rootIndex[i] = -2;
                continue;
            }

            if (probedAbsent is not null && probedAbsent.Contains(rootId)) continue;

            rootIndex[i] = roots.Count;
            roots.Add((i, rootId));
        }

        if (roots.Count > 0)
        {
            var ids = new Hash128[roots.Count];
            for (int k = 0; k < roots.Count; k++) ids[k] = roots[k].RootId;
            byte[] bm = await reader.EntitiesExistBitmapAsync(ids, ct).ConfigureAwait(false);
            List<Hash128>? confirmed = null;
            for (int k = 0; k < roots.Count; k++)
            {
                bool present = BitmapBits.IsSet(bm, k);
                if (!present)
                {
                    probedAbsent?.Add(roots[k].RootId);
                    continue;
                }
                int i = roots[k].Index;
                if (perFile is not null)
                {
                    (confirmed ??= []).Add(roots[k].RootId);
                    if (!perFile.IgnoreCompletedFiles)
                        presentFileRoots.Add((i, CompletionIdFor(records[i], roots[k].RootId)));
                    continue;
                }
                ApplyWitness(records[i], roots[k].RootId, handler, builder);
                reader.MarkProven([roots[k].RootId]);
                shortcircuited.Add((records[i], handler.UnitsPerRecord(records[i])));
                ReleaseNativeArtifacts(records[i], handler);
                rootIndex[i] = -2;
            }
            if (confirmed is { Count: > 0 }) reader.MarkProven(confirmed);
        }

        if (perFile is not null && presentFileRoots.Count > 0)
        {
            var candidates = presentFileRoots.Select(static x => x.CompletionId).Distinct().ToArray();
            var completed = await reader.HasSourcesCompletedAsync(
                candidates, perFile.LayerOrder, ct).ConfigureAwait(false);
            foreach (var (i, completionId) in presentFileRoots)
                if (completed.Contains(completionId))
                {
                    shortcircuited.Add((records[i], handler.UnitsPerRecord(records[i])));
                    rootIndex[i] = -2;
                    ReleaseNativeArtifacts(records[i], handler);
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

            if (probedAbsent is not null
                && ((!sProven && probedAbsent.Contains(sRoot))
                    || (!oProven && probedAbsent.Contains(oRoot))))
                continue;

            candidates.Add((i, sProven ? -1 : Slot(sRoot), oProven ? -1 : Slot(oRoot)));
        }

        if (probeIds.Count > 0)
        {
            byte[] bm = await reader.EntitiesExistBitmapAsync(probeIds, ct).ConfigureAwait(false);
            bool Present(int slot) => BitmapBits.IsSet(bm, slot);

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
        => TryResolveRoot(record, handler, out rootId, out _);

    private static bool TryResolveRoot<TRecord>(
        TRecord record, IIngestRecordHandler<TRecord> handler, out Hash128 rootId, out bool unresolvable)
    {
        rootId = default;
        unresolvable = false;
        if (record is GrammarIngestRecord gr && handler is GrammarIngestHandler grammar)
            return GrammarRowComposer.TryProbeRowRoot(
                gr.LineUtf8, gr.Ast, grammar.ModalityId, out rootId, out _);
        if (record is ContentIngestRecord cr && handler is ContentIngestHandler or DocumentIngestHandler)
        {
            if (cr.ContentRootId != default)
            {
                rootId = cr.ContentRootId;
                return true;
            }
            // Backward-compatible synthetic records historically stored the content root in
            // SourceId. New document records keep SourceId for structural provenance and fill
            // ContentRootId explicitly.
            if (cr.SourceId != default)
            {
                rootId = cr.SourceId;
                return true;
            }
            Hash128? id;
            try
            {
                id = TextDecomposer.ContentRootId(cr.CanonicalUtf8);
            }
            catch (InvalidOperationException ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "IngestExistenceGate: skipping record with unresolvable content root: {0}", ex.Message);
                unresolvable = true;
                return false;
            }
            if (id is null) return false;
            rootId = id.Value;
            return true;
        }
        if (record is GrammarComposeRecord gcr)
        {
            if (gcr.SourceId is not { } id || id == default) return false;
            rootId = id;
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

    private static void ReleaseNativeArtifacts<TRecord>(
        TRecord record, IIngestRecordHandler<TRecord> handler)
    {
        if (handler is GrammarIngestHandler && record is GrammarIngestRecord gr)
            gr.Ast.Dispose();
    }

}

public sealed class PresentRootDeferredUnit : IIngestDeferredUnit
{
    public static readonly PresentRootDeferredUnit Instance = new();
    public TierTree? TreeForBatchProbe => null;
    public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
        Task.FromResult<byte[]?>(null);
    public Hash128 DrainInto(SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap) =>
        default;
    public void Dispose() { }
}
