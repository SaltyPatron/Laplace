using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Audio composition: mono PCM16 → native audio ladder over the shared Tier-0 atoms
/// (codepoint→sample→window→onset→phrase→track) → O(tiers) existence probe → staging.
/// Same Tier-0 floor as <see cref="ContentTierSpine"/>; identity is the composed root,
/// never a hash of the PCM bytes. Sample/window/segment/track structures are reusable
/// perfcache maps that video and other containers consume unchanged.
/// </summary>
public static class AudioTierSpine
{
    /// <summary>Highest audio ladder tier (0=Codepoint … 5=Track).</summary>
    public const int MaxAudioTiers = 5;
    public const int MaxExistenceRounds = MaxAudioTiers + 1;

    private static readonly int RootMemoCapacity = IngestSizing.ResolveApplyIo(
        IngestTopology.Current.ApplyPartitions).AudioRootCacheIds;
    private static readonly ConcurrentDictionary<Hash128, Hash128?> RootMemo = new();
    private static int _rootMemoCount;

    public static TierTree? BuildTree(ReadOnlySpan<short> pcm) =>
        IntentStage.BuildAudioTree(pcm);

    /// <summary>
    /// Ladder root via native compose. The memo key hashes the PCM buffer only to cache
    /// the lookup; it is never returned as the entity id.
    /// </summary>
    public static Hash128? ResolveRoot(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty) return null;
        var memoKey = Hash128.Blake3(MemoryMarshal.AsBytes(pcm));
        if (RootMemo.TryGetValue(memoKey, out var cached)) return cached;
        Hash128? root = IntentStage.AudioRootId(pcm);
        if (Volatile.Read(ref _rootMemoCount) < RootMemoCapacity && RootMemo.TryAdd(memoKey, root))
        {
            int after = Interlocked.Increment(ref _rootMemoCount);
            if (after > RootMemoCapacity && RootMemo.TryRemove(memoKey, out _))
                Interlocked.Decrement(ref _rootMemoCount);
        }
        return root;
    }

    public static Task<byte[]?> ExistenceEmitBitmapAsync(
        TierTree tree, ISubstrateReader reader, CancellationToken ct = default)
    {
        var results = TierTreeDescent.ProbeBatchEmitBitmapsAsync([tree], reader, ct);
        return AwaitFirst(results);
    }

    public static Task<byte[]?[]> BatchExistenceEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader, CancellationToken ct = default) =>
        TierTreeDescent.ProbeBatchEmitBitmapsAsync(trees, reader, ct);

    private static async Task<byte[]?> AwaitFirst(Task<byte[]?[]> task)
    {
        var results = await task.ConfigureAwait(false);
        return results.Length > 0 ? results[0] : null;
    }

    public static bool EmitTree(
        SubstrateChangeBuilder builder,
        TierTree tree,
        Hash128 sourceId,
        ReadOnlySpan<byte> existenceBitmap,
        out Hash128 rootId) =>
        builder.ContentStage.EmitAudioTree(tree, sourceId, existenceBitmap, out rootId);

    public static bool EmitTree(
        IntentStage stage,
        TierTree tree,
        Hash128 sourceId,
        ReadOnlySpan<byte> existenceBitmap,
        out Hash128 rootId) =>
        stage.EmitAudioTree(tree, sourceId, existenceBitmap, out rootId);
}
