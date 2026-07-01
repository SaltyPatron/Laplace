using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe class MerkleDedup
{
    public static int FilterNovel(
        ReadOnlySpan<Hash128> candidates,
        ReadOnlySpan<byte> existingBitmap,
        Span<Hash128> outNovel)
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
        fixed (byte* pBm = existingBitmap)
        fixed (Hash128* pOut = outNovel)
        {
            rc = NativeInterop.MerkleDedupFilterNovel(
                pCand, (nuint)candidates.Length,
                pBm, (nuint)existingBitmap.Length * 8,
                pOut, &outN);
        }
        if (rc != 0) throw new InvalidOperationException("merkle_dedup_filter_novel failed");
        return checked((int)outN);
    }

    public static int TrunkShortcircuit(
        TierTree tree,
        ReadOnlySpan<byte> existingBitmap,
        Span<uint> outNovelIndices)
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
