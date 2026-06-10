using System.Collections.Concurrent;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>Compatibility shim — delegates to <see cref="ContentWitnessBatch"/>.</summary>
public static class ContentEmitter
{
    private const int MemoCap = 1 << 20;
    private static readonly ConcurrentDictionary<Hash128, Hash128?> _rootMemo = new();
    private static int _rootMemoCount;

    public static Hash128? Emit(SubstrateChangeBuilder b, string surface, Hash128 sourceId) =>
        ContentWitnessBatch.Emit(b, surface, sourceId);

    public static Hash128? Emit(SubstrateChangeBuilder b, byte[] canonical, Hash128 sourceId) =>
        ContentWitnessBatch.Emit(b, canonical, sourceId);

    public static Hash128? RootId(string surface)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        var bytes = Encoding.UTF8.GetBytes(surface);
        var key = Hash128.Blake3(bytes);
        if (_rootMemo.TryGetValue(key, out var cached)) return cached;
        Hash128? result = TextEntityBuilder.TryDecomposeRoot(bytes,
                out var rootId, out _, out _, out _, out _, out _)
            ? rootId : null;
        if (Volatile.Read(ref _rootMemoCount) < MemoCap && _rootMemo.TryAdd(key, result))
            Interlocked.Increment(ref _rootMemoCount);
        return result;
    }
}
