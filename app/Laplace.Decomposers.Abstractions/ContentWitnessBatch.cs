using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;


public static class ContentWitnessBatch
{
    private const int RootMemoCap = 1 << 20;
    private static readonly ConcurrentDictionary<Hash128, Hash128?> _rootMemo = new();
    private static int _rootMemoCount;

    public static Hash128? Emit(SubstrateChangeBuilder builder, string surface, Hash128 sourceId)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        return TryAppendToBuilder(builder, Encoding.UTF8.GetBytes(surface), sourceId, out var root)
            ? root : null;
    }

    public static Hash128? Emit(SubstrateChangeBuilder builder, byte[] canonical, Hash128 sourceId)
    {
        if (canonical.Length == 0) return null;
        return TryAppendToBuilder(builder, canonical, sourceId, out var root) ? root : null;
    }

    
    public static Hash128? RootId(ReadOnlySpan<byte> canonical)
    {
        if (canonical.IsEmpty) return null;
        var key = Hash128.Blake3(canonical);
        if (_rootMemo.TryGetValue(key, out var cached)) return cached;
        byte[] owned = canonical.ToArray();
        Hash128? result = TextEntityBuilder.TryDecomposeRoot(owned,
                out var rootId, out _, out _, out _, out _, out _)
            ? rootId : null;
        if (Volatile.Read(ref _rootMemoCount) < RootMemoCap && _rootMemo.TryAdd(key, result))
            Interlocked.Increment(ref _rootMemoCount);
        return result;
    }

    public static Hash128? RootId(string surface)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        return RootId(Encoding.UTF8.GetBytes(surface));
    }

    public static bool TryAddToIntentStage(
        IntentStage stage,
        ReadOnlySpan<byte> canonical,
        Hash128 sourceId,
        out Hash128 rootId) =>
        stage.TryAddContentWitness(canonical, sourceId, out rootId);

    public static bool TryAppendToBuilder(
        SubstrateChangeBuilder builder,
        ReadOnlySpan<byte> canonical,
        Hash128 sourceId,
        out Hash128 rootId) =>
        TryAddToIntentStage(builder.ContentStage, canonical, sourceId, out rootId);

    
    public static bool TryAppendUnderscoredToBuilder(
        SubstrateChangeBuilder builder,
        ReadOnlySpan<byte> underscoredUtf8,
        Hash128 sourceId,
        out Hash128 rootId)
    {
        rootId = default;
        if (underscoredUtf8.IsEmpty) return false;

        int underscores = 0;
        foreach (byte c in underscoredUtf8)
            if (c == (byte)'_') underscores++;

        if (underscores == 0)
            return TryAppendToBuilder(builder, underscoredUtf8, sourceId, out rootId);

        int len = underscoredUtf8.Length;
        if (len <= 512)
        {
            Span<byte> buf = stackalloc byte[len];
            CopyUnderscoreToSpace(underscoredUtf8, buf);
            return TryAppendToBuilder(builder, buf, sourceId, out rootId);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            CopyUnderscoreToSpace(underscoredUtf8, rented.AsSpan(0, len));
            return TryAppendToBuilder(builder, rented.AsSpan(0, len), sourceId, out rootId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void CopyUnderscoreToSpace(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int j = 0;
        foreach (byte c in src)
            dst[j++] = c == (byte)'_' ? (byte)' ' : c;
    }
}
