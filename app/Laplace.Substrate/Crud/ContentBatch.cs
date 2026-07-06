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
        public Hash128 Source;
        public Hash128 RootId;
        public TierTree? Tree;
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
        if (_reader.TryGetCachedRoot(key, out rootId))
            return true;

        if (_map.TryGetValue(key, out var existing))
        {
            rootId = existing.RootId;
            return true;
        }

        Hash128? cheapRoot = ContentTierSpine.ResolveRoot(canonical);
        if (cheapRoot is null) return false;
        rootId = cheapRoot.Value;

        if (_reader.IsProvenPresent(rootId))
        {
            _reader.CacheRoot(key, rootId);
            return true;
        }

        _map[key] = new Entry
        {
            Canonical = canonical.ToArray(),
            Source = sourceId,
            RootId = rootId,
            Tree = null,
        };
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

        byte[] rootBm = roots.Count > 0
            ? await _reader.EntitiesExistBitmapAsync(roots, ct).ConfigureAwait(false)
            : [];

        var probeTrees = new List<TierTree>();
        var emitEntries = new List<Entry>();
        long bits = (long)rootBm.Length * 8;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (i < bits && (rootBm[i >> 3] & (1 << (i & 7))) != 0)
            {
                _reader.MarkProven([e.RootId]);
                _reader.CacheRoot(Hash128.Blake3(e.Canonical), e.RootId);
                continue;
            }

            e.Tree ??= ContentTierSpine.BuildTree(e.Canonical);
            if (e.Tree is null) continue;
            probeTrees.Add(e.Tree);
            emitEntries.Add(e);
        }

        byte[]?[] bitmaps = probeTrees.Count > 0
            ? await ContentTierSpine.BatchExistenceEmitBitmapsAsync(probeTrees, _reader, ct).ConfigureAwait(false)
            : [];

        for (int t = 0; t < emitEntries.Count; t++)
        {
            var e = emitEntries[t];
            if (e.Tree is null) continue;
            byte[]? bm = t < bitmaps.Length ? bitmaps[t] : null;
            if (bm is { Length: > 0 })
            {
                for (int i = 0; i < bm.Length; i++)
                    if (bm[i] != 0) goto emit;
                bm = null;
            }
        emit:
            ContentTierSpine.EmitTree(stage, e.Tree, e.Source, bm ?? ReadOnlySpan<byte>.Empty, out _);
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
