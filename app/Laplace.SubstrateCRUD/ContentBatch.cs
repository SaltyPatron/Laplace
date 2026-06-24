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
        if (!_map.TryGetValue(key, out var e))
        {
            var tree = IntentStage.BuildContentTree(canonical);
            if (tree is null) return false;
            e = new Entry { Tree = tree, Source = sourceId };
            _map[key] = e;
            _bufferedNodes += tree.NodeCount;
        }
        rootId = e.Tree.GetNode(e.Tree.NaturalUnitIndex()).Id;

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

        // T0 codepoints (perfcache-guaranteed present) and T1 graphemes are NEVER checked: they are
        // known by construction and simply never enter the descent. Only trunks (tier >= 2 — words and
        // up) form the descent forest. The substrate is a content-addressed Merkle DAG, so a PRESENT
        // trunk implies its whole subtree is present; the descent therefore checks only nodes under a
        // novel parent and short-circuits present subtrees — O(tier-depth) per document, not O(nodes).
        //
        // Build the forest once: candidate ids + a parent-index array (parent's position in the
        // candidate list, < 0 = root or a parent below tier 2). nodeToCand[i][j] maps tree i's node j
        // to its candidate index (-1 if not a tier>=2 candidate).
        var candidates = new List<Hash128>();
        var parents = new List<int>();
        var nodeToCand = new int[entries.Count][];
        for (int i = 0; i < entries.Count; i++)
        {
            var ids = perTreeIds[i]; var tiers = perTreeTiers[i];
            var map = new int[ids.Length];
            for (int j = 0; j < ids.Length; j++) map[j] = -1;
            for (int j = 0; j < ids.Length; j++)
            {
                if (tiers[j] < 2) continue;
                map[j] = candidates.Count;
                candidates.Add(ids[j]);
                parents.Add(-1);            // resolved below, once every node has a candidate index
            }
            nodeToCand[i] = map;
        }
        for (int i = 0; i < entries.Count; i++)
        {
            var ids = perTreeIds[i]; var tiers = perTreeTiers[i];
            var map = nodeToCand[i]; var tree = entries[i].Tree;
            for (int j = 0; j < ids.Length; j++)
            {
                if (tiers[j] < 2) continue;
                uint pj = tree.GetNode((uint)j).ParentIdx;
                int parentCand = (pj == TierTree.Invalid || pj >= (uint)ids.Length) ? -1 : map[pj];
                parents[map[j]] = parentCand;   // -1 ⇒ this node is a descent root
            }
        }

        // One top-down server-side probe → a present-bitmap (bit c set ⟺ candidate c present),
        // identical contract to the flat entities_exist_bitmap, just O(tier) instead of O(nodes). The
        // bitmap and the trunk-descent live native (content_descent_bitmap builds the bits;
        // content_witness_emit_tree does the staging descent); C# only marshals the forest and walks
        // the result into each tree's bitmap, exactly as the original flat path did.
        byte[]? combined = candidates.Count > 0
            ? await _reader.ContentDescentBitmapAsync(candidates, parents, ct).ConfigureAwait(false)
            : null;
        long combinedBits = combined is null ? 0 : (long)combined.Length * 8;

        var stage = _stageProvider();
        for (int i = 0; i < entries.Count; i++)
        {
            var ids = perTreeIds[i]; var tiers = perTreeTiers[i];
            var map = nodeToCand[i];
            int n = ids.Length;
            var bm = new byte[(n + 7) / 8];   // bit j set = node j PRESENT (EmitContentTree skips present subtrees)
            for (int j = 0; j < n; j++)
            {
                if (tiers[j] < 2) continue;   // tier<=1: bit 0 (novel) — emitted under novel trunks, skipped under present
                int c = map[j];
                if (combined is not null && c >= 0 && c < combinedBits &&
                    (combined[c >> 3] & (1 << (c & 7))) != 0)
                    bm[j >> 3] |= (byte)(1 << (j & 7));
            }
            stage.EmitContentTree(entries[i].Tree, entries[i].Source, bm, out _);
        }

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
