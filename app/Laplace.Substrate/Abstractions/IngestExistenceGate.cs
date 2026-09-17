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

        // This gate owns durable COMPLETION receipts only. Entity/content presence is
        // Rule #8 step 5's one whole-working-set trunk->tier descent; doing a root
        // EntitiesExistBitmapAsync here creates a second novelty decision before that
        // descent and adds a database crossing that scales outside O(tiers).
        _ = builder;
        _ = probedAbsent;
        var shortcircuited = new List<(TRecord, long)>();
        var removed = new bool[records.Count];
        var perFile = handler as DocumentIngestHandler;

        // Explicit source-unit receipts belong to the atomic admission transaction.
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
                if (!completed.Contains(receiptId) || removed[i]) continue;
                shortcircuited.Add((records[i], handler.UnitsPerRecord(records[i])));
                ReleaseNativeArtifacts(records[i], handler);
                removed[i] = true;
            }
        }

        // Per-file document completion is a replay receipt, not an entity-presence
        // shortcut. Query the receipt directly. A completed file necessarily committed
        // its content in the accepted working set; requiring a separate content-root
        // presence query before trusting the receipt only duplicates step 5's authority.
        if (perFile is not null && !perFile.IgnoreCompletedFiles)
        {
            var candidates = new List<(int Index, Hash128 CompletionId, bool VendorOwned)>();
            for (int i = 0; i < records.Count; i++)
            {
                if (removed[i] || records[i] is IIngestCompletionRecord) continue;

                bool vendorOwned = records[i] is ContentIngestRecord { Metadata: not null };
                Hash128 completionId;
                if (records[i] is ContentIngestRecord cr && cr.FileId != default)
                {
                    completionId = cr.FileId;
                }
                else if (records[i] is ContentIngestRecord { Metadata: { } metadata } withMetadata)
                {
                    completionId = FileEntity.Resolve(withMetadata.CanonicalUtf8, metadata).FileId;
                }
                else if (TryResolveRoot(records[i], handler, out var rootId, out var unresolvable))
                {
                    completionId = rootId;
                }
                else
                {
                    // Preserve the existing invalid-root disposition: a record whose
                    // canonical root cannot be resolved cannot enter composition.
                    if (unresolvable)
                    {
                        removed[i] = true;
                        ReleaseNativeArtifacts(records[i], handler);
                    }
                    continue;
                }
                candidates.Add((i, completionId, vendorOwned));
            }

            if (candidates.Count > 0)
            {
                var ownedIds = candidates.Where(static x => x.VendorOwned)
                    .Select(static x => x.CompletionId).Distinct().ToArray();
                var legacyIds = candidates.Where(static x => !x.VendorOwned)
                    .Select(static x => x.CompletionId).Distinct().ToArray();
                IReadOnlySet<Hash128> owned = ownedIds.Length == 0
                    ? new HashSet<Hash128>()
                    : await reader.HasFilesCompletedAsync(
                        ownedIds, DocumentSource.SourceId, perFile.LayerOrder, ct).ConfigureAwait(false);
                IReadOnlySet<Hash128> legacy = legacyIds.Length == 0
                    ? new HashSet<Hash128>()
                    : await reader.HasSourcesCompletedAsync(
                        legacyIds, perFile.LayerOrder, ct).ConfigureAwait(false);

                foreach (var (i, completionId, vendorOwned) in candidates)
                {
                    if (removed[i] || !(vendorOwned ? owned : legacy).Contains(completionId)) continue;
                    shortcircuited.Add((records[i], handler.UnitsPerRecord(records[i])));
                    removed[i] = true;
                    ReleaseNativeArtifacts(records[i], handler);
                }
            }
        }

        var novel = new List<TRecord>(records.Count);
        for (int i = 0; i < records.Count; i++)
            if (!removed[i]) novel.Add(records[i]);
        records.Clear();
        records.AddRange(novel);
        return shortcircuited.ToArray();
    }

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
