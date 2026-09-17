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

        var presenceScope = reader.CapturePresenceScope();
        var shortcircuited = new List<(TRecord, long)>();
        var perFile = handler as DocumentIngestHandler;
        var roots = new List<(int Index, Hash128 RootId)>();
        var presentFileRoots = new List<(int Index, Hash128 CompletionId)>();
        var rootIndex = new int[records.Count];
        Array.Fill(rootIndex, -1);

        // These receipts belong to the atomic admission transaction. In contrast,
        // an entity may survive a failed apply because its COPY committed separately.
        // Group by relation type so the reader can prune attestation partitions.
        var completionRecords = new Dictionary<Hash128, List<(int Index, Hash128 ReceiptId)>>();
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i] is not IIngestCompletionRecord completed) continue;
            var typeId = completed.CompletionAttestationTypeId;
            var receiptId = completed.CompletionAttestationId;
            if (typeId == default || receiptId == default) continue;
            if (!completionRecords.TryGetValue(typeId, out var candidates))
                completionRecords[typeId] = candidates = [];
            candidates.Add((i, receiptId));
        }
        foreach (var (typeId, candidates) in completionRecords)
        {
            var ids = candidates.Select(static x => x.ReceiptId).Distinct().ToArray();
            var completed = await reader.PresentAttestationIdsAsync(typeId, ids, ct)
                .ConfigureAwait(false);
            foreach (var (i, receiptId) in candidates)
            {
                if (!completed.Contains(receiptId)) continue;
                shortcircuited.Add((records[i], handler.UnitsPerRecord(records[i])));
                ReleaseNativeArtifacts(records[i], handler);
                rootIndex[i] = -2;
            }
        }

        static Hash128 CompletionIdFor(TRecord record, Hash128 contentRoot)
        {
            if (record is not ContentIngestRecord cr) return contentRoot;
            if (cr.FileId != default) return cr.FileId;
            return cr.Metadata is { } metadata
                ? FileEntity.Resolve(cr.CanonicalUtf8, metadata).FileId
                : contentRoot;
        }

        // Whole-record skipping needs a matching durable receipt. Content, grammar and
        // relation records without one still compose: their entity-presence bitmaps
        // suppress only canonical entity insertion, while native emission retains raw forms.
        // Content presence and file completion are different identities. The content root
        // answers whether this entity is present; it does not prove DAG completion. The file-composition id answers
        // whether THIS occurrence (content + identity metadata) already completed.
        for (int i = 0; i < records.Count; i++)
        {
            if (rootIndex[i] == -2 || records[i] is IIngestCompletionRecord || perFile is null) continue;
            if (!TryResolveRoot(records[i], handler, out var rootId, out var unresolvable))
            {
                if (unresolvable) rootIndex[i] = -2;
                continue;
            }

            if (reader.IsProvenPresent(rootId))
            {
                if (!perFile.IgnoreCompletedFiles)
                    presentFileRoots.Add((i, CompletionIdFor(records[i], rootId)));
                continue;
            }

            if (probedAbsent is not null && probedAbsent.Contains(rootId)) continue;

            rootIndex[i] = roots.Count;
            roots.Add((i, rootId));
        }

        if (perFile is not null && roots.Count > 0)
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
                (confirmed ??= []).Add(roots[k].RootId);
                if (!perFile.IgnoreCompletedFiles)
                    presentFileRoots.Add((i, CompletionIdFor(records[i], roots[k].RootId)));
            }
            if (confirmed is { Count: > 0 }) reader.MarkProven(confirmed, presenceScope);
        }

        if (perFile is not null && presentFileRoots.Count > 0)
        {
            bool VendorOwned(int i) =>
                records[i] is ContentIngestRecord { Metadata: not null };
            var ownedIds = presentFileRoots.Where(x => VendorOwned(x.Index))
                .Select(static x => x.CompletionId).Distinct().ToArray();
            var legacyIds = presentFileRoots.Where(x => !VendorOwned(x.Index))
                .Select(static x => x.CompletionId).Distinct().ToArray();
            IReadOnlySet<Hash128> owned = ownedIds.Length == 0
                ? new HashSet<Hash128>()
                : await reader.HasFilesCompletedAsync(
                    ownedIds, DocumentSource.SourceId, perFile.LayerOrder, ct).ConfigureAwait(false);
            IReadOnlySet<Hash128> legacy = legacyIds.Length == 0
                ? new HashSet<Hash128>()
                : await reader.HasSourcesCompletedAsync(
                    legacyIds, perFile.LayerOrder, ct).ConfigureAwait(false);
            foreach (var (i, completionId) in presentFileRoots)
                if ((VendorOwned(i) ? owned : legacy).Contains(completionId))
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

    private static bool TryResolveRoot<TRecord>(
        TRecord record, IIngestRecordHandler<TRecord> handler, out Hash128 rootId)
        => TryResolveRoot(record, handler, out rootId, out _);

    private static bool TryResolveRoot<TRecord>(
        TRecord record, IIngestRecordHandler<TRecord> handler, out Hash128 rootId, out bool unresolvable)
    {
        rootId = default;
        unresolvable = false;
        if (record is ContentIngestRecord cr && handler is DocumentIngestHandler)
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
        // A known plain-text root does not prove that a full source grammar has
        // been admitted. Its syntax/gap composition must resolve through the same
        // native source recipe before an existence decision can be made.
        if (record is GrammarComposeRecord) return false;
        // A generic trunk identity does not prove source-unit completion.
        // Explicit completion receipts are checked before this content-only gate.
        return false;
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
