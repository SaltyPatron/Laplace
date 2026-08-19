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
    internal readonly record struct RootCoord(double X, double Y, double Z, double M)
    {
        public void CopyTo(Span<double> destination)
        {
            if (destination.Length < 4)
                throw new ArgumentException("destination needs 4 doubles", nameof(destination));
            destination[0] = X;
            destination[1] = Y;
            destination[2] = Z;
            destination[3] = M;
        }
    }

    /// <summary>
    /// Cached trees are held for process lifetime. Reserve their actual native capacity,
    /// UTF-8 text, and managed key bytes from the generic run-cache envelope; there is no
    /// unrelated distinct-surface count or string-length cap. The caller admits only
    /// source-governed repeat classes (currently collection tags), rather than guessing
    /// reuse from a magic byte length.
    /// </summary>
    private static readonly long CacheBudgetBytes = IngestSizing.ResolveApplyIo(
        IngestTopology.Current.ApplyPartitions).CacheBytesPerOwner;

    private sealed record CachedTree(TierTree Tree, long ResidentBytes);
    private static readonly ConcurrentDictionary<string, CachedTree> Trees = new(StringComparer.Ordinal);
    private static int _count;
    private static long _residentBytes;

    /// <summary>Distinct surfaces currently memoized (diagnostics/tests).</summary>
    internal static int CachedSurfaceCount => Volatile.Read(ref _count);

    internal static void Clear()
    {
        foreach (var kv in Trees)
            if (Trees.TryRemove(kv.Key, out var cached))
            {
                Interlocked.Decrement(ref _count);
                Interlocked.Add(ref _residentBytes, -cached.ResidentBytes);
                cached.Tree.Dispose();
            }
    }

    /// <summary>
    /// Build (or cache-hit) the tier tree for <paramref name="surface"/> without touching a
    /// builder. Safe on the compose fan. When <paramref name="callerOwns"/> is true the
    /// caller must Dispose the tree after emit; when false the process cache owns it.
    /// </summary>
    public static bool TryBuild(
        string surface, bool retainForReuse, out TierTree tree, out bool callerOwns)
    {
        tree = null!;
        callerOwns = false;
        if (string.IsNullOrEmpty(surface)) return false;

        if (Trees.TryGetValue(surface, out var hit))
        {
            tree = hit.Tree;
            return true;
        }

        int byteLen = Encoding.UTF8.GetByteCount(surface);
        int max = Encoding.UTF8.GetMaxByteCount(surface.Length);
        TierTree? built;
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

        if (built is null) return false;

        long residentBytes = checked(
            (long)built.Capacity * MemoryTopology.TierTreeResidentBytesPerCapacity
            + byteLen + (long)surface.Length * sizeof(char));
        bool reserved = retainForReuse && TryReserve(residentBytes);
        if (reserved
            && Trees.TryAdd(surface, new CachedTree(built, residentBytes)))
        {
            Interlocked.Increment(ref _count);
            tree = built;
            callerOwns = false;
            return true;
        }

        // Budget miss, non-reusable surface, or lost publish race: caller owns the tree.
        if (reserved)
        {
            Interlocked.Add(ref _residentBytes, -residentBytes);
            if (Trees.TryGetValue(surface, out hit))
            {
                built.Dispose();
                tree = hit.Tree;
                callerOwns = false;
                return true;
            }
        }

        tree = built;
        callerOwns = true;
        return true;
    }

    private static bool TryReserve(long bytes)
    {
        while (true)
        {
            long before = Volatile.Read(ref _residentBytes);
            if (bytes > CacheBudgetBytes - before) return false;
            if (Interlocked.CompareExchange(ref _residentBytes, before + bytes, before) == before)
                return true;
        }
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
    public static bool TryRootCoord(TierTree tree, out RootCoord coord)
    {
        coord = default;
        var node = tree.GetNode(tree.NaturalUnitIndex());
        unsafe
        {
            if (node.Coord[0] == 0.0 && node.Coord[1] == 0.0
                && node.Coord[2] == 0.0 && node.Coord[3] == 0.0) return false;
            coord = new RootCoord(node.Coord[0], node.Coord[1], node.Coord[2], node.Coord[3]);
        }
        return true;
    }

    /// <summary>
    /// Stage one collection member and return the coordinate produced by that exact
    /// tree. Collection correctness therefore does not depend on the tree surviving in
    /// a process-global cache between compose and drain.
    /// </summary>
    public static bool TryStageWithCoord(
        SubstrateChangeBuilder builder, string surface, Hash128 sourceId,
        bool retainForReuse, out Hash128 rootId, out RootCoord coord)
    {
        rootId = default;
        coord = default;
        if (string.IsNullOrEmpty(surface) || builder.DeferredContent is not null)
            return false;
        if (!TryBuild(surface, retainForReuse, out var tree, out bool owned))
            return false;
        try
        {
            return TryEmit(builder, tree, sourceId, ReadOnlySpan<byte>.Empty, out rootId)
                && TryRootCoord(tree, out coord);
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

        if (!TryBuild(surface, retainForReuse: false, out var tree, out bool owned))
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
