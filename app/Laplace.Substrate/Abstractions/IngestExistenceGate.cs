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

        // This gate reads durable completion receipts only. Content presence is decided by
        // the one working-set existence descent; a root probe here would be a second
        // novelty decision and a database crossing outside O(tiers).
        _ = builder;
        _ = probedAbsent;
        var shortcircuited = new List<(TRecord, long)>();
        var removed = new bool[records.Count];
        var perFile = handler as DocumentIngestHandler;

        // Explicit source-unit completion receipts are written in the admission transaction.
        var completionRecords = new List<(int Index, IngestUnitCompletionKey Key)>();
        for (int i = 0; i < records.Count; i++)
        {
            if (records[i] is not IIngestCompletionRecord { Completion: { } key }) continue;
            // An unspecified witness or unit identity cannot authorize a skip.
            if (key.WitnessId == default || key.UnitId == default) continue;
            completionRecords.Add((i, key));
        }
        if (completionRecords.Count > 0)
        {
            var keys = completionRecords.Select(static x => x.Key).Distinct().ToArray();
            var completed = await reader.CompletedUnitsAsync(keys, ct).ConfigureAwait(false);
            foreach (var (i, key) in completionRecords)
            {
                if (!completed.Contains(key) || removed[i]) continue;
                shortcircuited.Add((records[i], handler.UnitsPerRecord(records[i])));
                ReleaseNativeArtifacts(records[i], handler);
                removed[i] = true;
            }
        }

        // Per-file document completion is a receipt, not an entity-presence shortcut: a
        // completed file committed its content in the same working set, so the receipt
        // alone skips it.
        if (perFile is not null && !perFile.IgnoreCompletedFiles)
        {
            var candidates = new List<(int Index, Hash128 CompletionId)>();
            for (int i = 0; i < records.Count; i++)
            {
                if (removed[i] || records[i] is IIngestCompletionRecord) continue;

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
                    // A record whose canonical root cannot be resolved cannot enter composition.
                    if (unresolvable)
                    {
                        removed[i] = true;
                        ReleaseNativeArtifacts(records[i], handler);
                    }
                    continue;
                }
                candidates.Add((i, completionId));
            }

            if (candidates.Count > 0)
            {
                // Every document file completion is witnessed by the document source.
                var ids = candidates.Select(static x => x.CompletionId).Distinct().ToArray();
                IReadOnlySet<Hash128> done = await reader.HasFilesCompletedAsync(
                    ids, DocumentSource.SourceId, perFile.LayerOrder, ct).ConfigureAwait(false);

                foreach (var (i, completionId) in candidates)
                {
                    if (removed[i] || !done.Contains(completionId)) continue;
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
            // A record without ContentRootId carries its content root in SourceId.
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
        // Explicit unit completions are checked before this content-only gate.
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
