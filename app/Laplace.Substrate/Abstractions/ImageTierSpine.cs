using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Composes recovered planar RGBA natively over the shared Tier-0 codepoint atoms
/// (digit → number → channel → pixel → patch/region → image), then runs one existence
/// descent in O(tiers) rounds. The grammar above Tier 0 differs from
/// <see cref="ContentTierSpine"/>; the atoms and the composition law are the same.
/// Identity is the composition, never a hash of the raw buffer: an equal pixel or patch
/// composition is one entity wherever it occurs, whatever its parent or tier.
/// </summary>
public static class ImageTierSpine
{
    /// <summary>Highest image ladder tier (0=codepoint … 6=Image).</summary>
    public const int MaxImageTiers = 6;
    public const int MaxExistenceRounds = MaxImageTiers + 1;

    private static readonly int RootMemoCapacity = IngestSizing.ResolveApplyIo(
        IngestTopology.Current.ApplyPartitions).ImageRootCacheIds;
    private static readonly ConcurrentDictionary<Hash128, Hash128?> RootMemo = new();
    private static int _rootMemoCount;

    public static TierTree? BuildTree(ReadOnlySpan<byte> rgba, uint width, uint height) =>
        IntentStage.BuildImageTree(rgba, width, height);

    /// <summary>
    /// Root id via native compose. The bounded memo is keyed by a hash of the buffer;
    /// that key is never returned as the entity id.
    /// </summary>
    public static Hash128? ResolveRoot(ReadOnlySpan<byte> rgba, uint width, uint height)
    {
        if (rgba.IsEmpty || width == 0 || height == 0) return null;
        var memoKey = Hash128.Blake3(rgba);
        if (RootMemo.TryGetValue(memoKey, out var cached)) return cached;
        Hash128? root = IntentStage.ImageRootId(rgba, width, height);
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
        builder.ContentStage.EmitImageTree(tree, sourceId, existenceBitmap, out rootId);

    public static bool EmitTree(
        IntentStage stage,
        TierTree tree,
        Hash128 sourceId,
        ReadOnlySpan<byte> existenceBitmap,
        out Hash128 rootId) =>
        stage.EmitImageTree(tree, sourceId, existenceBitmap, out rootId);
}
