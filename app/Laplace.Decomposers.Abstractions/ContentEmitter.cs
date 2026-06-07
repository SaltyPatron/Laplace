using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class ContentEmitter
{
    private const int MemoCap = 1 << 20;

    private const int MemoMaxContentBytes = 64;

    private static readonly ConcurrentDictionary<(Hash128 Src, Hash128 Content),
        (Hash128 Root, ImmutableArray<EntityRow> Ents, ImmutableArray<PhysicalityRow> Phys)> _emitMemo = new();

    private static readonly ConcurrentDictionary<Hash128, Hash128?> _rootMemo = new();

    private static int _emitMemoCount;
    private static int _rootMemoCount;

    public static Hash128? Emit(SubstrateChangeBuilder b, string surface, Hash128 sourceId)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        return Emit(b, Encoding.UTF8.GetBytes(surface), sourceId);
    }

    public static Hash128? Emit(SubstrateChangeBuilder b, byte[] canonical, Hash128 sourceId)
    {
        if (canonical.Length == 0) return null;

        var contentHash = Hash128.Blake3(canonical);
        var key = (sourceId, contentHash);

        if (_emitMemo.TryGetValue(key, out var hit))
        {
            foreach (var e in hit.Ents) b.AddEntity(e);
            foreach (var p in hit.Phys) b.AddPhysicality(p);
            return hit.Root;
        }

        if (!TextEntityBuilder.TryBuildRows(canonical, sourceId,
                out var entities, out var physicalities, out var rootId, out _))
        {
            MemoRoot(contentHash, null);
            return null;
        }

        var seededT0 = new HashSet<Hash128>();
        foreach (var e in entities) if (e.Tier == 0) seededT0.Add(e.Id);
        var ents = entities.Where(e => e.Tier != 0).ToImmutableArray();
        var phys = physicalities.Where(p => !seededT0.Contains(p.EntityId)).ToImmutableArray();

        foreach (var e in ents) b.AddEntity(e);
        foreach (var p in phys) b.AddPhysicality(p);

        if (canonical.Length <= MemoMaxContentBytes
            && Volatile.Read(ref _emitMemoCount) < MemoCap
            && _emitMemo.TryAdd(key, (rootId, ents, phys)))
        {
            Interlocked.Increment(ref _emitMemoCount);
        }
        MemoRoot(contentHash, rootId);
        return rootId;
    }

    private static void MemoRoot(Hash128 contentHash, Hash128? rootId)
    {
        if (Volatile.Read(ref _rootMemoCount) < MemoCap && _rootMemo.TryAdd(contentHash, rootId))
            Interlocked.Increment(ref _rootMemoCount);
    }

    public static Hash128? RootId(string surface)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        var bytes = Encoding.UTF8.GetBytes(surface);
        var contentHash = Hash128.Blake3(bytes);

        if (_rootMemo.TryGetValue(contentHash, out var cached)) return cached;

        Hash128? result = TextEntityBuilder.TryDecomposeRoot(bytes,
                out var rootId, out _, out _, out _, out _, out _)
            ? rootId : (Hash128?)null;
        MemoRoot(contentHash, result);
        return result;
    }
}
