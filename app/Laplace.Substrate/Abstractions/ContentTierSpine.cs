using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// The shared text content path of the ingest recipe. Compose: UTF-8 → UAX #29 tier tree
/// (native TextDecomposer + perfcache-backed HashComposer). Converge: trunk-to-leaf batch
/// existence, at most <see cref="MaxContentTier"/> + 1 round trips per probe batch (tiers
/// 4 down to 0; tier 0 answers from the perfcache before PostgreSQL). Stage: Merkle-DAG
/// emit via <c>merkle_dedup_trunk_shortcircuit</c>. Providers hand records in; every
/// source's text reaches the same identities through this path.
/// </summary>
public static class ContentTierSpine
{
    /// <summary>Highest document tier in the text ladder (0=codepoint … 4=document).</summary>
    public const int MaxContentTier = 4;

    /// <summary>Maximum tier-scoped existence rounds per probe batch (inclusive 0..4).</summary>
    public const int MaxExistenceRounds = MaxContentTier + 1;

    private static readonly int RootMemoCapacity = IngestSizing.ResolveApplyIo(
        IngestTopology.Current.ApplyPartitions).TextRootCacheIds;
    private static readonly ConcurrentDictionary<Hash128, Hash128?> RootMemo = new();
    private static int _rootMemoCount;

    /// <summary>Leaf-to-trunk compose: UAX #29 segmentation + Merkle ids (CPU only).</summary>
    public static TierTree? BuildTree(ReadOnlySpan<byte> canonicalUtf8)
    {
        // Composing an identity is valid in a cold process, so the codepoint
        // perfcache is loaded here rather than assumed from a prior ingest.
        CodepointPerfcache.LoadDefault();
        return IntentStage.BuildContentTree(canonicalUtf8);
    }

    /// <summary>
    /// Leaf-to-trunk compose of authored source bytes. The tier law is the same; only
    /// Unicode normalization is off, so the source round-trips byte-exactly. Parser or
    /// grammar structure is separate evidence over this trajectory.
    /// </summary>
    public static TierTree? BuildSourceTree(ReadOnlySpan<byte> sourceUtf8)
    {
        CodepointPerfcache.LoadDefault();
        return IntentStage.BuildSourceContentTree(sourceUtf8);
    }

    /// <summary>
    /// Canonical root for authored source bytes, independent of the parser or grammar provider.
    /// </summary>
    public static Hash128? ResolveSourceRoot(ReadOnlySpan<byte> sourceUtf8)
    {
        if (sourceUtf8.IsEmpty) return null;
        CodepointPerfcache.LoadDefault();
        return TextDecomposer.SourceRootId(sourceUtf8);
    }

    /// <summary>Root id without building a full tree when the native fast path applies.</summary>
    /// <remarks>
    /// Returns null, never throws, for content the native path rejects (malformed
    /// encoding, rc&lt;0), so a caller can skip one bad file without failing the batch.
    /// </remarks>
    public static Hash128? ResolveRoot(ReadOnlySpan<byte> canonicalUtf8)
    {
        if (canonicalUtf8.IsEmpty) return null;
        CodepointPerfcache.LoadDefault();
        var key = Hash128.Blake3(canonicalUtf8);
        if (RootMemo.TryGetValue(key, out var cached)) return cached;
        Hash128? cheap;
        try
        {
            cheap = TextDecomposer.ContentRootId(canonicalUtf8);
        }
        catch (InvalidOperationException)
        {
            // Native rc=-5 (decomposer failure) and kin — not a cacheable root.
            cheap = null;
        }
        if (cheap is not null)
        {
            TryMemoize(key, cheap);
            return cheap;
        }
        byte[] owned = canonicalUtf8.ToArray();
        Hash128? result = TextEntityBuilder.TryDecomposeRoot(
                owned, out var rootId, out _, out _, out _, out _, out _)
            ? rootId : null;
        TryMemoize(key, result);
        return result;
    }

    public static Hash128? ResolveRoot(string surface)
    {
        if (string.IsNullOrEmpty(surface)) return null;
        return ResolveRoot(Encoding.UTF8.GetBytes(surface));
    }

    private static void TryMemoize(Hash128 key, Hash128? root)
    {
        if (Volatile.Read(ref _rootMemoCount) >= RootMemoCapacity) return;
        if (!RootMemo.TryAdd(key, root)) return;
        int after = Interlocked.Increment(ref _rootMemoCount);
        if (after <= RootMemoCapacity) return;
        if (RootMemo.TryRemove(key, out _)) Interlocked.Decrement(ref _rootMemoCount);
    }

    /// <summary>O(tiers) trunk-to-leaf existence for one composed tree.</summary>
    public static Task<byte[]?> ExistenceEmitBitmapAsync(
        TierTree tree, ISubstrateReader reader, CancellationToken ct = default)
    {
        var results = TierTreeDescent.ProbeBatchEmitBitmapsAsync([tree], reader, ct);
        return AwaitFirst(results);
    }

    public static async Task<byte[]?> ExistenceEmitBitmapAsync(
        TierTree tree, ISubstrateReader reader, ISet<Hash128>? probedAbsent, CancellationToken ct = default)
    {
        var results = await TierTreeDescent.ProbeBatchEmitBitmapsAsync([tree], reader, probedAbsent, ct)
            .ConfigureAwait(false);
        return results[0];
    }

    /// <summary>O(tiers) trunk-to-leaf existence across many trees in one batch (distinct ids deduped per round).</summary>
    public static Task<byte[]?[]> BatchExistenceEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader, CancellationToken ct = default) =>
        TierTreeDescent.ProbeBatchEmitBitmapsAsync(trees, reader, ct);

    public static Task<byte[]?[]> BatchExistenceEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader, ISet<Hash128>? probedAbsent, CancellationToken ct = default) =>
        TierTreeDescent.ProbeBatchEmitBitmapsAsync(trees, reader, probedAbsent, ct);

    private static async Task<byte[]?> AwaitFirst(Task<byte[]?[]> task)
    {
        var results = await task.ConfigureAwait(false);
        return results.Length > 0 ? results[0] : null;
    }

    /// <summary>Emit source forms; the existence bitmap suppresses duplicate entity insertion.</summary>
    public static bool EmitTree(
        SubstrateChangeBuilder builder,
        TierTree tree,
        Hash128 sourceId,
        ReadOnlySpan<byte> existenceBitmap,
        out Hash128 rootId) =>
        builder.ContentStage.EmitContentTree(tree, sourceId, existenceBitmap, out rootId);

    public static bool EmitTree(
        IntentStage stage,
        TierTree tree,
        Hash128 sourceId,
        ReadOnlySpan<byte> existenceBitmap,
        out Hash128 rootId) =>
        stage.EmitContentTree(tree, sourceId, existenceBitmap, out rootId);

    /// <summary>
    /// Stages authored source bytes through the source-preserving tier ladder: the same
    /// constituent/trajectory law as <see cref="TryStageIntoBuilder"/>, with NFC
    /// normalization off so the exact source stays reconstructable.
    /// </summary>
    public static bool TryStageSourceIntoBuilder(
        SubstrateChangeBuilder builder,
        ReadOnlySpan<byte> sourceUtf8,
        Hash128 sourceId,
        out Hash128 rootId)
    {
        rootId = default;
        if (sourceUtf8.IsEmpty) return false;
        using var tree = BuildSourceTree(sourceUtf8);
        return tree is not null
            && EmitTree(builder, tree, sourceId, ReadOnlySpan<byte>.Empty, out rootId);
    }

    public static bool TryStageIntoBuilder(
        SubstrateChangeBuilder builder,
        ReadOnlySpan<byte> canonicalUtf8,
        Hash128 sourceId,
        out Hash128 rootId)
    {
        rootId = default;
        if (canonicalUtf8.IsEmpty) return false;

        if (builder.DeferredContent is { } cb)
            return cb.Append(canonicalUtf8, sourceId, out rootId);

        return builder.ContentStage.TryAddContentWitness(canonicalUtf8, sourceId, out rootId);
    }

    public static bool TryStageUnderscoredIntoBuilder(
        SubstrateChangeBuilder builder,
        ReadOnlySpan<byte> underscoredUtf8,
        Hash128 sourceId,
        out Hash128 rootId)
    {
        rootId = default;
        if (underscoredUtf8.IsEmpty) return false;
        bool hasUnderscore = false;
        foreach (byte c in underscoredUtf8)
            if (c == (byte)'_') { hasUnderscore = true; break; }
        if (!hasUnderscore)
            return TryStageIntoBuilder(builder, underscoredUtf8, sourceId, out rootId);
        int len = underscoredUtf8.Length;
        if (len <= 512)
        {
            Span<byte> buf = stackalloc byte[len];
            CopyUnderscoreToSpace(underscoredUtf8, buf);
            return TryStageIntoBuilder(builder, buf, sourceId, out rootId);
        }
        byte[] rented = ArrayPool<byte>.Shared.Rent(len);
        try
        {
            CopyUnderscoreToSpace(underscoredUtf8, rented.AsSpan(0, len));
            return TryStageIntoBuilder(builder, rented.AsSpan(0, len), sourceId, out rootId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static void CopyUnderscoreToSpace(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int j = 0;
        foreach (byte c in src)
            dst[j++] = c == (byte)'_' ? (byte)' ' : c;
    }
}
