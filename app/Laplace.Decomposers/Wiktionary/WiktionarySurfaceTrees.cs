using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// Build-once / emit-many for Wiktionary surfaces — the text-lane counterpart of the
/// chess T0 vocabulary cache.
///
/// <para>
/// <see cref="ContentTierSpine.TryStageIntoBuilder"/> welds two very different costs
/// into one call: deriving the tier tree (decompose to codepoints, walk the grapheme
/// ladder, Merkle-hash every node) and registering that tree with a builder. Native
/// <c>content_witness_emit_tree</c> dedups by root id, but only AFTER the root has been
/// derived (content_witness_batch.c:364) — so the whole ladder is re-walked for every
/// occurrence and then thrown away.
/// </para>
/// <para>
/// Wiktionary calls the welded path for every word, gloss, example, tag, synonym,
/// antonym, hypernym, translation, IPA, form and etymology term across ~10.5M entries.
/// Its high-frequency surfaces — POS tags, register tags, language codes, function
/// words — are a small finite alphabet re-derived millions of times, exactly the shape
/// laplace_t0_perfcache exists for at the codepoint tier.
/// </para>
/// <para>
/// UD already separates the two halves (UdIngestAdapter.EnsureTrees/DrainInto); this is
/// the same split, plus a cache across records because Wiktionary's repeat class spans
/// entries rather than living inside one sentence.
/// </para>
/// </summary>
internal static class WiktionarySurfaceTrees
{
    /// <summary>
    /// Only SHORT surfaces are cached. Glosses, examples and etymology text are long and
    /// very nearly unique — caching them would spend native tree memory for no hit rate
    /// (memo only the repeat class of the key space). Words, tags, POS labels and
    /// language codes all sit comfortably under this.
    /// </summary>
    private const int MaxCachedSurfaceBytes = 64;

    /// <summary>
    /// Bound on distinct cached surfaces. Cached trees are held for process lifetime, so
    /// this is the memory ceiling: past it, callers fall back to build-emit-dispose and
    /// simply lose the reuse, never correctness.
    /// </summary>
    private const int Cap = 1 << 16;

    private static readonly ConcurrentDictionary<string, TierTree> Trees = new(StringComparer.Ordinal);
    private static int _count;

    /// <summary>Distinct surfaces currently memoized (diagnostics/tests).</summary>
    internal static int CachedSurfaceCount => Volatile.Read(ref _count);

    internal static void Clear()
    {
        foreach (var kv in Trees)
            if (Trees.TryRemove(kv.Key, out var t))
            {
                Interlocked.Decrement(ref _count);
                t.Dispose();
            }
    }

    public static bool TryStage(
        SubstrateChangeBuilder builder, string surface, Hash128 sourceId, out Hash128 rootId)
    {
        rootId = default;
        if (string.IsNullOrEmpty(surface)) return false;

        // A deferred-content builder routes staging through ContentBatch.Append, which
        // EmitTree does not go through. Leave that lane exactly as it was.
        if (builder.DeferredContent is not null)
            return StageDirect(builder, surface, sourceId, out rootId);

        if (Trees.TryGetValue(surface, out var hit))
            return ContentTierSpine.EmitTree(builder, hit, sourceId, ReadOnlySpan<byte>.Empty, out rootId);

        int byteLen = Encoding.UTF8.GetByteCount(surface);
        if (byteLen > MaxCachedSurfaceBytes)
            return StageDirect(builder, surface, sourceId, out rootId);

        Span<byte> buf = stackalloc byte[MaxCachedSurfaceBytes];
        int n = Encoding.UTF8.GetBytes(surface, buf);

        var tree = ContentTierSpine.BuildTree(buf[..n]);
        if (tree is null) return false;

        // Publish first so a concurrent builder of the same surface can reuse it. The
        // loser of the race (or an over-cap build) emits from its own tree and disposes
        // it — the cache never owns two trees for one key, and no tree is ever disposed
        // while still reachable from the map.
        bool owned = false;
        if (Volatile.Read(ref _count) < Cap && Trees.TryAdd(surface, tree))
        {
            Interlocked.Increment(ref _count);
            owned = true;
        }

        try
        {
            return ContentTierSpine.EmitTree(builder, tree, sourceId, ReadOnlySpan<byte>.Empty, out rootId);
        }
        finally
        {
            if (!owned) tree.Dispose();
        }
    }

    /// <summary>
    /// Uncached staging without the per-call <c>Encoding.UTF8.GetBytes</c> heap allocation
    /// the old <c>Stage</c> paid on every surface — the spine's own
    /// <see cref="ContentTierSpine.TryStageUnderscoredIntoBuilder"/> uses this same
    /// stackalloc/ArrayPool shape, and the API takes a span precisely so callers need not
    /// allocate.
    /// </summary>
    private static bool StageDirect(
        SubstrateChangeBuilder builder, string surface, Hash128 sourceId, out Hash128 rootId)
    {
        int max = Encoding.UTF8.GetMaxByteCount(surface.Length);
        if (max <= 512)
        {
            Span<byte> small = stackalloc byte[512];
            int written = Encoding.UTF8.GetBytes(surface, small);
            return ContentTierSpine.TryStageIntoBuilder(builder, small[..written], sourceId, out rootId);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            int written = Encoding.UTF8.GetBytes(surface, rented);
            return ContentTierSpine.TryStageIntoBuilder(builder, rented.AsSpan(0, written), sourceId, out rootId);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
