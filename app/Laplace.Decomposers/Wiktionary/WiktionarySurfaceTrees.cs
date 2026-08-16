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
/// entries rather than living inside one sentence. The bulk ingest handler builds via
/// <see cref="TryBuild"/> on the compose fan and emits via <see cref="TryEmit"/> in
/// serial DrainInto — <see cref="TryStage"/> remains for the grammar-witness path.
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

    /// <summary>
    /// Build (or cache-hit) the tier tree for <paramref name="surface"/> without touching a
    /// builder. Safe on the compose fan. When <paramref name="callerOwns"/> is true the
    /// caller must Dispose the tree after emit; when false the process cache owns it.
    /// </summary>
    public static bool TryBuild(string surface, out TierTree tree, out bool callerOwns)
    {
        tree = null!;
        callerOwns = false;
        if (string.IsNullOrEmpty(surface)) return false;

        if (Trees.TryGetValue(surface, out var hit))
        {
            tree = hit;
            return true;
        }

        int byteLen = Encoding.UTF8.GetByteCount(surface);
        TierTree? built;
        if (byteLen <= MaxCachedSurfaceBytes)
        {
            Span<byte> buf = stackalloc byte[MaxCachedSurfaceBytes];
            int n = Encoding.UTF8.GetBytes(surface, buf);
            built = ContentTierSpine.BuildTree(buf[..n]);
        }
        else
        {
            int max = Encoding.UTF8.GetMaxByteCount(surface.Length);
            if (max <= 512)
            {
                Span<byte> small = stackalloc byte[512];
                int written = Encoding.UTF8.GetBytes(surface, small);
                built = ContentTierSpine.BuildTree(small[..written]);
            }
            else
            {
                byte[] rented = ArrayPool<byte>.Shared.Rent(max);
                try
                {
                    int written = Encoding.UTF8.GetBytes(surface, rented);
                    built = ContentTierSpine.BuildTree(rented.AsSpan(0, written));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        if (built is null) return false;

        if (byteLen <= MaxCachedSurfaceBytes
            && Volatile.Read(ref _count) < Cap
            && Trees.TryAdd(surface, built))
        {
            Interlocked.Increment(ref _count);
            tree = built;
            callerOwns = false;
            return true;
        }

        // Over-cap, long surface, or lost the publish race: caller owns and disposes.
        if (byteLen <= MaxCachedSurfaceBytes && Trees.TryGetValue(surface, out hit))
        {
            built.Dispose();
            tree = hit;
            callerOwns = false;
            return true;
        }

        tree = built;
        callerOwns = true;
        return true;
    }

    /// <summary>
    /// The live coordinate of <paramref name="surface"/>'s stored identity — the collapsed
    /// natural unit of its tier tree.
    /// </summary>
    /// <remarks>
    /// Needed because a set composition's coordinate is the centroid of its MEMBERS' live
    /// coordinates (INVENTION §9), and a member emitted in an earlier batch is not among this
    /// builder's staged physicalities. Tag surfaces are short, so they are always in the process
    /// cache and this is a hit plus one native node read.
    ///
    /// Returns false when the tree carries no composed geometry yet — the coord array is zeroed
    /// at build and filled by the composer, and the origin is not a placement. A caller that
    /// gets false must not substitute a default; the origin would forge a centroid.
    /// </remarks>
    public static bool TryRootCoord(string surface, Span<double> coordXyzm)
    {
        if (coordXyzm.Length < 4) throw new ArgumentException("coordXyzm needs 4 doubles", nameof(coordXyzm));
        if (string.IsNullOrEmpty(surface)) return false;
        if (!TryBuild(surface, out var tree, out bool owned)) return false;
        try
        {
            var node = tree.GetNode(tree.NaturalUnitIndex());
            unsafe
            {
                if (node.Coord[0] == 0.0 && node.Coord[1] == 0.0
                    && node.Coord[2] == 0.0 && node.Coord[3] == 0.0) return false;
                for (int i = 0; i < 4; i++) coordXyzm[i] = node.Coord[i];
            }
            return true;
        }
        finally
        {
            if (owned) tree.Dispose();
        }
    }

    public static bool TryEmit(
        SubstrateChangeBuilder builder, TierTree tree, Hash128 sourceId,
        ReadOnlySpan<byte> existingBitmap, out Hash128 rootId) =>
        ContentTierSpine.EmitTree(builder, tree, sourceId, existingBitmap, out rootId);

    public static bool TryStage(
        SubstrateChangeBuilder builder, string surface, Hash128 sourceId, out Hash128 rootId)
    {
        rootId = default;
        if (string.IsNullOrEmpty(surface)) return false;

        // A deferred-content builder routes staging through ContentBatch.Append, which
        // EmitTree does not go through. Leave that lane exactly as it was.
        if (builder.DeferredContent is not null)
            return StageDirect(builder, surface, sourceId, out rootId);

        if (!TryBuild(surface, out var tree, out bool owned))
            return false;

        try
        {
            return TryEmit(builder, tree, sourceId, ReadOnlySpan<byte>.Empty, out rootId);
        }
        finally
        {
            if (owned) tree.Dispose();
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
