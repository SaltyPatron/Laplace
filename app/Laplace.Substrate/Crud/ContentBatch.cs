using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Deferred content batching for imperative-compose lanes. All tree build,
/// O(tiers) existence, and Merkle emit delegate to <see cref="ContentTierSpine"/>.
/// </summary>
public sealed class ContentBatch : IDisposable
{
    private sealed class Entry
    {
        public required byte[] Canonical;
        public readonly List<Hash128> Sources = new();
        private readonly HashSet<Hash128> _seenSources = new();
        public Hash128 RootId;
        public TierTree? Tree;
        public byte[]? ExistingBitmap;

        public void ObserveSource(Hash128 source)
        {
            if (_seenSources.Add(source)) Sources.Add(source);
        }
    }

    private readonly Dictionary<Hash128, Entry> _map = new();
    private readonly Func<IntentStage> _stageProvider;
    private readonly ISubstrateReader _reader;

    public ContentBatch(Func<IntentStage> stageProvider, ISubstrateReader reader)
    {
        _stageProvider = stageProvider ?? throw new ArgumentNullException(nameof(stageProvider));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public bool HasPending => _map.Count > 0;

    public bool Append(ReadOnlySpan<byte> canonical, Hash128 sourceId, out Hash128 rootId)
    {
        rootId = default;
        if (canonical.IsEmpty) return false;

        var key = Hash128.Blake3(canonical);
        if (_map.TryGetValue(key, out var existing))
        {
            existing.ObserveSource(sourceId);
            rootId = existing.RootId;
            return true;
        }

        if (!_reader.TryGetCachedRoot(key, out rootId))
        {
            Hash128? cheapRoot = ContentTierSpine.ResolveRoot(canonical);
            if (cheapRoot is null) return false;
            rootId = cheapRoot.Value;
        }

        var entry = new Entry
        {
            Canonical = canonical.ToArray(),
            RootId = rootId,
            Tree = null,
        };
        entry.ObserveSource(sourceId);
        _map[key] = entry;
        return true;
    }

    public Task ProbeAndFlushAsync(CancellationToken ct) => ProbeAndEmitAsync(ct);

    private async Task ProbeAndEmitAsync(CancellationToken ct)
    {
        if (_map.Count == 0) return;

        var entries = new List<Entry>(_map.Count);
        foreach (var e in _map.Values)
            entries.Add(e);

        var stage = _stageProvider();
        var roots = new List<Hash128>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
            roots.Add(entries[i].RootId);

        var presenceScope = _reader.CapturePresenceScope();
        byte[] rootBm = roots.Count > 0
            ? await _reader.EntitiesExistBitmapAsync(roots, ct).ConfigureAwait(false)
            : [];

        var probeTrees = new List<TierTree>();
        var emitEntries = new List<Entry>();
        var rootsProvenAbsent = new HashSet<Hash128>();

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            e.Tree ??= ContentTierSpine.BuildTree(e.Canonical);
            if (e.Tree is null)
                throw new InvalidOperationException("previously resolved content could not produce its native tree");
            if (BitmapBits.IsSet(rootBm, i))
            {
                _reader.MarkProven([e.RootId], presenceScope);
                _reader.CacheRoot(Hash128.Blake3(e.Canonical), e.RootId);
                // Keep the real root proof for native entity filtering; its
                // physicality observations still belong to this source unit.
                e.ExistingBitmap = new byte[(e.Tree.NodeCount + 7) / 8];
                int rootIndex = checked((int)e.Tree.NaturalUnitIndex());
                e.ExistingBitmap[rootIndex >> 3] |= (byte)(1 << (rootIndex & 7));
                continue;
            }

            // The unscoped root probe above already proved this identity absent.
            // Carry that result into the tier descent so its highest-tier round
            // does not immediately issue the same indexed lookup a second time.
            rootsProvenAbsent.Add(e.RootId);

            probeTrees.Add(e.Tree);
            emitEntries.Add(e);
        }

        byte[]?[] bitmaps = probeTrees.Count > 0
            ? await ContentTierSpine.BatchExistenceEmitBitmapsAsync(
                probeTrees, _reader, rootsProvenAbsent, ct).ConfigureAwait(false)
            : [];

        for (int t = 0; t < emitEntries.Count; t++)
            emitEntries[t].ExistingBitmap = t < bitmaps.Length ? bitmaps[t] : null;

        foreach (var e in entries)
        {
            foreach (Hash128 source in e.Sources)
                if (!ContentTierSpine.EmitTree(stage, e.Tree!, source,
                        e.ExistingBitmap ?? ReadOnlySpan<byte>.Empty, out var emittedRoot)
                    || emittedRoot != e.RootId)
                    throw new InvalidOperationException("native content observation emission failed or changed its root identity");
            _reader.CacheRoot(Hash128.Blake3(e.Canonical), e.RootId);
        }

        foreach (var e in _map.Values) e.Tree?.Dispose();
        _map.Clear();
    }

    public void Dispose()
    {
        foreach (var e in _map.Values) e.Tree?.Dispose();
        _map.Clear();
    }
}
