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
        var perTree = new List<Hash128[]>(_map.Count);
        int total = 0;
        foreach (var e in _map.Values)
        {
            var ids = e.Tree.NodeIds();
            entries.Add(e);
            perTree.Add(ids);
            total += ids.Length;
        }

        byte[]? combined = null;
        long combinedBits = 0;
        if (total > 0)
        {
            var candidates = new Hash128[total];
            int off = 0;
            foreach (var ids in perTree)
            {
                Array.Copy(ids, 0, candidates, off, ids.Length);
                off += ids.Length;
            }
            combined = await _reader.EntitiesExistBitmapAsync(candidates, ct).ConfigureAwait(false);
            combinedBits = (long)combined.Length * 8;
        }

        var stage = _stageProvider();
        int g = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            int n = perTree[i].Length;
            if (combined is not null && n > 0)
            {
                var bm = new byte[(n + 7) / 8];
                for (int j = 0; j < n; j++)
                {
                    int gi = g + j;
                    if (gi < combinedBits && (combined[gi >> 3] & (1 << (gi & 7))) != 0)
                        bm[j >> 3] |= (byte)(1 << (j & 7));
                }
                stage.EmitContentTree(entries[i].Tree, entries[i].Source, bm, out _);
            }
            else
            {
                stage.EmitContentTree(entries[i].Tree, entries[i].Source, ReadOnlySpan<byte>.Empty, out _);
            }
            g += n;
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
