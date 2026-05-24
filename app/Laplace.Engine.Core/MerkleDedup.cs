using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

/// <summary>
/// Managed wrappers over the engine merkle_dedup primitives
/// (engine/core/include/laplace/core/merkle_dedup.h). Hot-loop helpers for
/// <c>Laplace.SubstrateCRUD.NpgsqlSubstrateWriter</c> per ADR 0050.
///
/// Bitmap convention: packed LSB-first within each byte; bit i is at
/// <c>(bitmap[i >> 3] >> (i &amp; 7)) &amp; 1u</c>. Caller is responsible
/// for ensuring bitmap capacity covers the candidate count / node count.
/// </summary>
public static unsafe class MerkleDedup
{
    /// <summary>Filter <paramref name="candidates"/> into novel-only output
    /// per <paramref name="existingBitmap"/>. Returns the count of novel
    /// hashes written into <paramref name="outNovel"/>.
    ///
    /// Order-preserving: novel hashes appear in their original candidate
    /// order. <paramref name="outNovel"/> must have capacity ≥
    /// <paramref name="candidates"/>.Length.</summary>
    public static int FilterNovel(
        ReadOnlySpan<Hash128> candidates,
        ReadOnlySpan<byte>    existingBitmap,
        Span<Hash128>         outNovel)
    {
        if (outNovel.Length < candidates.Length)
            throw new ArgumentException("outNovel must have capacity >= candidates.Length", nameof(outNovel));
        if (candidates.Length == 0) return 0;

        int requiredBitmapBytes = (candidates.Length + 7) / 8;
        if (existingBitmap.Length < requiredBitmapBytes)
            throw new ArgumentException(
                $"existingBitmap must have at least {requiredBitmapBytes} bytes for {candidates.Length} candidates",
                nameof(existingBitmap));

        nuint outN = 0;
        int rc;
        fixed (Hash128* pCand = candidates)
        fixed (byte*    pBm   = existingBitmap)
        fixed (Hash128* pOut  = outNovel)
        {
            rc = NativeInterop.MerkleDedupFilterNovel(
                pCand, (nuint)candidates.Length,
                pBm,   (nuint)existingBitmap.Length * 8,
                pOut, &outN);
        }
        if (rc != 0) throw new InvalidOperationException("merkle_dedup_filter_novel failed");
        return checked((int)outN);
    }

    /// <summary>Trunk-shortcircuit walk over <paramref name="tree"/>:
    /// emit indices of nodes that are novel (clear bit) AND not under an
    /// ancestor whose bit is set. Returns the count of indices written.
    ///
    /// Caller must have called <see cref="TierTree.FinalizeParents"/> on
    /// the tree first (parent_idx must be populated). Assumes the SubstrateCRUD
    /// invariant "parent in substrate ⇒ all named descendants in substrate"
    /// per ADR 0050 + the header documentation.</summary>
    public static int TrunkShortcircuit(
        TierTree           tree,
        ReadOnlySpan<byte> existingBitmap,
        Span<uint>         outNovelIndices)
    {
        ArgumentNullException.ThrowIfNull(tree);
        int nodeCount = tree.NodeCount;
        if (outNovelIndices.Length < nodeCount)
            throw new ArgumentException("outNovelIndices must have capacity >= NodeCount", nameof(outNovelIndices));
        if (nodeCount == 0) return 0;
        int requiredBitmapBytes = (nodeCount + 7) / 8;
        if (existingBitmap.Length < requiredBitmapBytes)
            throw new ArgumentException(
                $"existingBitmap must have at least {requiredBitmapBytes} bytes for {nodeCount} nodes",
                nameof(existingBitmap));

        nuint outN = 0;
        int rc;
        bool added = false;
        try
        {
            tree.DangerousAddRef(ref added);
            fixed (byte* pBm = existingBitmap)
            fixed (uint* pOut = outNovelIndices)
            {
                rc = NativeInterop.MerkleDedupTrunkShortcircuit(
                    tree.DangerousNativeHandle,
                    pBm, (nuint)existingBitmap.Length * 8,
                    pOut, &outN);
            }
        }
        finally
        {
            if (added) tree.DangerousRelease();
        }
        if (rc != 0) throw new InvalidOperationException("merkle_dedup_trunk_shortcircuit failed");
        return checked((int)outN);
    }
}
