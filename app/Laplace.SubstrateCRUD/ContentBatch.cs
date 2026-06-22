using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Per-batch content tier-tree cache for the two-phase containment dedup path. The collect phase
/// (<see cref="Append"/>) builds — once, deduped by the Blake3 of the canonical bytes — the content
/// tier tree for each distinct content text and returns its natural-unit root id WITHOUT staging.
/// <see cref="ProbeAndFlushAsync"/> then runs one <c>entities_exist_bitmap</c> probe over every
/// tree's node ids and stages only the novel subtrees (a present trunk skips its whole subtree via
/// <c>MerkleDedup.TrunkShortcircuit</c> inside the native content emit) — no second decomposition.
///
/// This mirrors the UD decomposer's content two-phase, but in a single forward walk: content
/// emitters keep calling <c>ContentWitnessBatch.TryAppendToBuilder</c>, which routes here when a
/// builder has deferred content enabled. Trees are native handles, released on flush/<see cref="Dispose"/>.
/// </summary>
public sealed class ContentBatch : IDisposable
{
    private struct Entry
    {
        public TierTree Tree;
        public Hash128 Source;
        public byte[]? Bitmap;
    }

    private readonly Dictionary<Hash128, Entry> _map = new();

    public bool HasPending => _map.Count > 0;

    /// <summary>
    /// Build (once) the content tier tree for <paramref name="canonical"/> and return its natural-unit
    /// root id so attestations can be wired immediately; staging is deferred to
    /// <see cref="ProbeAndFlushAsync"/>. The tree is self-contained after build, so <paramref name="canonical"/>
    /// need not outlive this call.
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
            e = new Entry { Tree = tree, Source = sourceId, Bitmap = null };
            _map[key] = e;
        }
        rootId = e.Tree.GetNode(e.Tree.NaturalUnitIndex()).Id;
        return true;
    }

    /// <summary>
    /// One batched <c>entities_exist_bitmap</c> probe over all collected trees' node ids, then stage
    /// only the novel subtrees into <paramref name="stage"/>. Disposes the trees afterward.
    /// </summary>
    public async Task ProbeAndFlushAsync(IntentStage stage, ISubstrateReader reader, CancellationToken ct)
    {
        if (_map.Count == 0) return;

        var keys = new List<Hash128>(_map.Count);
        var perTree = new List<Hash128[]>(_map.Count);
        int total = 0;
        foreach (var kv in _map)
        {
            var ids = kv.Value.Tree.NodeIds();
            if (ids.Length == 0) continue;
            keys.Add(kv.Key);
            perTree.Add(ids);
            total += ids.Length;
        }

        if (total > 0)
        {
            var candidates = new Hash128[total];
            int off = 0;
            foreach (var ids in perTree)
            {
                Array.Copy(ids, 0, candidates, off, ids.Length);
                off += ids.Length;
            }

            byte[] combined = await reader.EntitiesExistBitmapAsync(candidates, ct);
            long combinedBits = (long)combined.Length * 8;

            int g = 0;
            for (int i = 0; i < keys.Count; i++)
            {
                int n = perTree[i].Length;
                var bm = new byte[(n + 7) / 8];
                for (int j = 0; j < n; j++)
                {
                    int gi = g + j;
                    if (gi < combinedBits && (combined[gi >> 3] & (1 << (gi & 7))) != 0)
                        bm[j >> 3] |= (byte)(1 << (j & 7));
                }
                var e = _map[keys[i]];
                e.Bitmap = bm;
                _map[keys[i]] = e;
                g += n;
            }
        }

        foreach (var e in _map.Values)
            stage.EmitContentTree(e.Tree, e.Source, e.Bitmap ?? ReadOnlySpan<byte>.Empty, out _);

        Dispose();
    }

    public void Dispose()
    {
        foreach (var e in _map.Values) e.Tree?.Dispose();
        _map.Clear();
    }
}
