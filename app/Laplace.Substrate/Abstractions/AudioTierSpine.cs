using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Audio content path: mono PCM16 recovery → native audio ladder above shared
/// codepoint T0 (codepoint→sample→window→onset→phrase→track) → O(tiers)
/// existence → modality witness emit. Sibling of <see cref="ContentTierSpine"/>;
/// same T0 floor. Do not blake3(pcm) as identity.
/// </summary>
public static class AudioTierSpine
{
    /// <summary>Highest audio ladder tier (0=Codepoint … 5=Track).</summary>
    public const int MaxAudioTiers = 5;
    public const int MaxExistenceRounds = MaxAudioTiers + 1;

    private const int RootMemoCap = 1 << 18;
    private static readonly ConcurrentDictionary<Hash128, Hash128?> RootMemo = new();
    private static int _rootMemoCount;

    public static TierTree? BuildTree(ReadOnlySpan<short> pcm) =>
        IntentStage.BuildAudioTree(pcm);

    /// <summary>
    /// Ladder root via native compose. Memo key is an opaque cache key over the
    /// recovery buffer — never returned as the entity id.
    /// </summary>
    public static Hash128? ResolveRoot(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty) return null;
        var memoKey = Hash128.Blake3(MemoryMarshal.AsBytes(pcm));
        if (RootMemo.TryGetValue(memoKey, out var cached)) return cached;
        Hash128? root = IntentStage.AudioRootId(pcm);
        if (Volatile.Read(ref _rootMemoCount) < RootMemoCap && RootMemo.TryAdd(memoKey, root))
            Interlocked.Increment(ref _rootMemoCount);
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
