using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Per-batch content tier-tree cache for the two-phase containment dedup path. The collect phase
/// (<see cref="Append"/>) builds — once, deduped by the Blake3 of the canonical bytes — the content
/// tier tree for each distinct content text and returns its natural-unit root id WITHOUT staging.
/// <see cref="ProbeAndFlushAsync"/> (and the bounded auto-flush below) then runs one
/// <c>entities_exist_bitmap</c> probe over the buffered trees' node ids and stages only the novel
/// subtrees (a present trunk skips its whole subtree via <c>MerkleDedup.TrunkShortcircuit</c> inside
/// the native content emit) — no second decomposition.
///
/// This mirrors the UD decomposer's content two-phase, but in a single forward walk: content
/// emitters keep calling <c>ContentWitnessBatch.TryAppendToBuilder</c>, which routes here when a
/// builder has deferred content enabled.
///
/// BOUNDED MEMORY: a single builder batch can be enormous (WordNet uses LAPLACE_INGEST_BATCH=65536
/// synsets/builder, each emitting several content surfaces), so buffering EVERY distinct content
/// tree until one end-of-batch flush held hundreds of thousands of native tier-tree handles at once
/// and exhausted the native heap — an abrupt access-violation crash with no managed exception. The
/// buffer is therefore flushed in bounded chunks (<see cref="MaxBufferedNodes"/>): each chunk is a
/// self-contained probe + emit + free, so peak resident trees stay bounded. The shared content stage
/// keeps its witness set across chunks, so a node emitted in an earlier chunk is never emitted twice;
/// dedup semantics are byte-for-byte identical to the single-flush version.
/// </summary>
public sealed class ContentBatch : IDisposable
{
    // Cap on total buffered tier-tree nodes before an automatic chunk flush. Each node is a few
    // native struct fields; 64Ki nodes keeps peak well under a few MB of trees + probe arrays while
    // still amortizing the existence probe over a large chunk (a handful of extra round trips for a
    // 65536-synset WordNet builder).
    private const int MaxBufferedNodes = 65536;

    private struct Entry
    {
        public TierTree Tree;
        public Hash128 Source;
    }

    private readonly Dictionary<Hash128, Entry> _map = new();
    private readonly Func<IntentStage> _stageProvider;
    private readonly ISubstrateReader _reader;
    private long _bufferedNodes;

    public ContentBatch(Func<IntentStage> stageProvider, ISubstrateReader reader)
    {
        _stageProvider = stageProvider ?? throw new ArgumentNullException(nameof(stageProvider));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public bool HasPending => _map.Count > 0;

    /// <summary>
    /// Build (once) the content tier tree for <paramref name="canonical"/> and return its natural-unit
    /// root id so attestations can be wired immediately; staging is deferred to the next bounded
    /// auto-flush or to <see cref="ProbeAndFlushAsync"/>. The tree is self-contained after build, so
    /// <paramref name="canonical"/> need not outlive this call.
    /// </summary>
    public bool Append(ReadOnlySpan<byte> canonical, Hash128 sourceId, out Hash128 rootId)
    {
        rootId = default;
        if (canonical.IsEmpty) return false;

        var key = Hash128.Blake3(canonical);

        // compose IS the dedup: a canonical already composed this session has a known, immutable root —
        // skip the expensive BuildContentTree (BLAKE3 + geometry per node) and hand back the cached root
        // so the caller still wires THIS occurrence's attestation. Only NOVEL canonicals pay the build.
        if (_reader.TryGetCachedRoot(key, out rootId))
            return true;

        if (!_map.TryGetValue(key, out var e))
        {
            var tree = IntentStage.BuildContentTree(canonical);
            if (tree is null) return false;
            e = new Entry { Tree = tree, Source = sourceId };
            _map[key] = e;
            _bufferedNodes += tree.NodeCount;
        }
        rootId = e.Tree.GetNode(e.Tree.NaturalUnitIndex()).Id;
        _reader.CacheRoot(key, rootId);

        // Bounded auto-flush: keep the resident native tier-tree set small even when a single builder
        // emits hundreds of thousands of distinct content units. The probe is async; this is a rare,
        // coarse-grained checkpoint (every MaxBufferedNodes nodes) so the synchronous wait is cheap and
        // — with ConfigureAwait(false) inside ProbeAndEmitAsync — cannot deadlock on a captured context.
        if (_bufferedNodes >= MaxBufferedNodes)
            ProbeAndEmitAsync(CancellationToken.None).GetAwaiter().GetResult();
        return true;
    }

    /// <summary>
    /// Flush any remaining buffered trees: one batched <c>entities_exist_bitmap</c> probe over their
    /// node ids, then stage only the novel subtrees into the builder's content stage. Idempotent.
    /// </summary>
    public Task ProbeAndFlushAsync(CancellationToken ct) => ProbeAndEmitAsync(ct);

    private async Task ProbeAndEmitAsync(CancellationToken ct)
    {
        if (_map.Count == 0) return;

        var entries = new List<Entry>(_map.Count);
        var perTreeIds = new List<Hash128[]>(_map.Count);
        var perTreeTiers = new List<byte[]>(_map.Count);
        foreach (var e in _map.Values)
        {
            var ids = e.Tree.NodeIds();
            var tiers = new byte[ids.Length];
            for (int j = 0; j < ids.Length; j++)
                tiers[j] = e.Tree.GetNode((uint)j).Tier;
            entries.Add(e);
            perTreeIds.Add(ids);
            perTreeTiers.Add(tiers);
        }

        // T0 codepoints + T1 graphemes are NEVER checked — perfcache-known by construction. Only trunks
        // (tier >= 2) are candidates. The reader's session seen-set fronts the probe: a re-emitted trunk
        // (content is immutable ⇒ proven-present is permanent) is an in-memory hit and never re-probes
        // the DB. One batched entities_exist_bitmap over the UNKNOWN trunks ("which of these do you
        // have?"); present (seen-set ∪ DB) → bit set → the native emit skips that subtree.
        var candidates = new List<Hash128>();
        for (int i = 0; i < entries.Count; i++)
        {
            var ids = perTreeIds[i]; var tiers = perTreeTiers[i];
            for (int j = 0; j < ids.Length; j++)
                if (tiers[j] >= 2) candidates.Add(ids[j]);
        }

        byte[]? combined = candidates.Count > 0
            ? await _reader.EntitiesExistBitmapAsync(candidates, ct).ConfigureAwait(false)
            : null;
        long combinedBits = combined is null ? 0 : (long)combined.Length * 8;

        var stage = _stageProvider();
        int g = 0;   // running index into the tier>=2 candidate bitmap, in (tree, node) order
        for (int i = 0; i < entries.Count; i++)
        {
            var ids = perTreeIds[i]; var tiers = perTreeTiers[i];
            int n = ids.Length;
            var bm = new byte[(n + 7) / 8];   // bit j set = node j PRESENT (EmitContentTree skips present subtrees)
            for (int j = 0; j < n; j++)
            {
                if (tiers[j] < 2) continue;   // tier<=1: emitted under novel trunks, skipped under present
                if (combined is not null && g < combinedBits && (combined[g >> 3] & (1 << (g & 7))) != 0)
                    bm[j >> 3] |= (byte)(1 << (j & 7));
                g++;
            }
            stage.EmitContentTree(entries[i].Tree, entries[i].Source, bm, out _);
        }

        // After staging, every checked trunk is present — the present ones were, the novel ones are now
        // staged (immutable ⇒ they WILL be inserted). Mark them all proven so the NEXT occurrence is a
        // seen-set hit: zero DB probe, zero re-stage. This is the re-emission tax going to zero.
        if (candidates.Count > 0) _reader.MarkProven(candidates);

        foreach (var e in _map.Values) e.Tree?.Dispose();
        _map.Clear();
        _bufferedNodes = 0;
    }

    public void Dispose()
    {
        foreach (var e in _map.Values) e.Tree?.Dispose();
        _map.Clear();
        _bufferedNodes = 0;
    }
}
