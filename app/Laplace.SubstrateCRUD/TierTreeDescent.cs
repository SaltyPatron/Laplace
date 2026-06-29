using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

/// <summary>
/// Shared O(tier) Merkle descent probe helpers for tier trees (content + grammar compose).
/// </summary>
public static class TierTreeDescent
{
    public static void BuildProbe(
        TierTree tree,
        out List<Hash128> ids,
        out List<int> parents,
        out int[] treeIdxToFlat)
    {
        int n = tree.NodeCount;
        treeIdxToFlat = new int[n];
        Array.Fill(treeIdxToFlat, -1);
        ids = new List<Hash128>();
        parents = new List<int>();

        for (int j = 0; j < n; j++)
        {
            if (tree.GetNode((uint)j).Tier < 2) continue;
            treeIdxToFlat[j] = ids.Count;
            ids.Add(tree.GetNode((uint)j).Id);
        }

        for (int j = 0; j < n; j++)
        {
            int flat = treeIdxToFlat[j];
            if (flat < 0) continue;
            uint p = tree.GetNode((uint)j).ParentIdx;
            int parentFlat = -1;
            while (p != TierTree.Invalid && p < (uint)n)
            {
                if (treeIdxToFlat[p] >= 0)
                {
                    parentFlat = treeIdxToFlat[p];
                    break;
                }
                p = tree.GetNode(p).ParentIdx;
            }
            parents.Add(parentFlat);
        }
    }

    /// <summary>
    /// Tier 0/1 node ids for a flat <see cref="ISubstrateReader.EntitiesExistBitmapAsync"/>
    /// probe — descent only covers tier&gt;=2 trunks; without these bits,
    /// <see cref="MerkleDedup.TrunkShortcircuit"/> still treats unmarked ancestors as novel.
    /// </summary>
    public static void BuildTier01Probe(
        TierTree tree, out List<Hash128> ids, out List<int> nodeIndices)
    {
        int n = tree.NodeCount;
        ids = new List<Hash128>();
        nodeIndices = new List<int>();
        for (int j = 0; j < n; j++)
        {
            if (tree.GetNode((uint)j).Tier >= 2) continue;
            nodeIndices.Add(j);
            ids.Add(tree.GetNode((uint)j).Id);
        }
    }

    /// <summary>
    /// OR tier 0/1 flat-probe hits into a per-node emit bitmap (same index order as
    /// <paramref name="nodeIndices"/> / flat bitmap).
    /// </summary>
    public static void ApplyTier01Present(byte[] emitBm, IReadOnlyList<int> nodeIndices, byte[] flatBm)
    {
        long bits = (long)flatBm.Length * 8;
        for (int k = 0; k < nodeIndices.Count; k++)
        {
            if (k >= bits || (flatBm[k >> 3] & (1 << (k & 7))) == 0) continue;
            int j = nodeIndices[k];
            emitBm[j >> 3] |= (byte)(1 << (j & 7));
        }
    }

    /// <summary>
    /// Map a <c>content_descent_bitmap</c> result (tier&gt;=2 candidate order) to a per-node
    /// emit bitmap (bit j set = node j present).
    /// When <paramref name="treeIdxToFlat"/> is set, indexes into a merged batch probe bitmap.
    /// </summary>
    public static byte[] NodeEmitBitmap(TierTree tree, byte[] descentBm, int[]? treeIdxToFlat = null)
    {
        int n = tree.NodeCount;
        var bm = new byte[(n + 7) / 8];
        long descentBits = (long)descentBm.Length * 8;

        if (treeIdxToFlat is null)
        {
            int g = 0;
            for (int j = 0; j < n; j++)
            {
                if (tree.GetNode((uint)j).Tier < 2) continue;
                if (g < descentBits && (descentBm[g >> 3] & (1 << (g & 7))) != 0)
                    bm[j >> 3] |= (byte)(1 << (j & 7));
                g++;
            }
        }
        else
        {
            for (int j = 0; j < n; j++)
            {
                int flat = treeIdxToFlat[j];
                if (flat < 0) continue;
                if (flat < descentBits && (descentBm[flat >> 3] & (1 << (flat & 7))) != 0)
                    bm[j >> 3] |= (byte)(1 << (j & 7));
            }
        }
        return bm;
    }

    /// <summary>
    /// Merge tier&gt;=2 trunk ids + parent indices from multiple trees into one flat candidate list
    /// for a single <c>content_descent_bitmap</c> round-trip. Parent indices refer to positions in
    /// the merged <paramref name="ids"/> list.
    /// </summary>
    public static void BuildBatchProbe(
        IReadOnlyList<TierTree> trees,
        out List<Hash128> ids,
        out List<int> parents,
        out int[][] treeIdxToFlatPerTree)
    {
        int treeCount = trees.Count;
        treeIdxToFlatPerTree = new int[treeCount][];
        ids = new List<Hash128>();
        parents = new List<int>();

        for (int t = 0; t < treeCount; t++)
        {
            var tree = trees[t];
            int n = tree.NodeCount;
            var treeIdxToFlat = new int[n];
            Array.Fill(treeIdxToFlat, -1);

            for (int j = 0; j < n; j++)
            {
                if (tree.GetNode((uint)j).Tier < 2) continue;
                treeIdxToFlat[j] = ids.Count;
                ids.Add(tree.GetNode((uint)j).Id);
            }
            treeIdxToFlatPerTree[t] = treeIdxToFlat;

            for (int j = 0; j < n; j++)
            {
                int flat = treeIdxToFlat[j];
                if (flat < 0) continue;
                uint p = tree.GetNode((uint)j).ParentIdx;
                int parentFlat = -1;
                while (p != TierTree.Invalid && p < (uint)n)
                {
                    if (treeIdxToFlat[p] >= 0)
                    {
                        parentFlat = treeIdxToFlat[p];
                        break;
                    }
                    p = tree.GetNode(p).ParentIdx;
                }
                parents.Add(parentFlat);
            }
        }
    }

    /// <summary>
    /// Tier 0 node ids resolved by the T0 perfcache — O(1) client-side, no DB round trip.
    /// Tier 1 (UAX#29 graphemes) is excluded; those still use <see cref="BuildBatchTier1Probe"/>.
    /// </summary>
    public static void BuildBatchTier0PerfcachePresent(
        IReadOnlyList<TierTree> trees,
        byte[][] perTreeEmitBm,
        out List<(int TreeIndex, int NodeIndex)> unresolvedTier0)
    {
        unresolvedTier0 = new List<(int, int)>();
        for (int t = 0; t < trees.Count; t++)
        {
            var tree = trees[t];
            int n = tree.NodeCount;
            for (int j = 0; j < n; j++)
            {
                if (tree.GetNode((uint)j).Tier != 0) continue;
                var id = tree.GetNode((uint)j).Id;
                if (CodepointPerfcache.IsKnownCodepointId(id))
                    perTreeEmitBm[t][j >> 3] |= (byte)(1 << (j & 7));
                else
                    unresolvedTier0.Add((t, j));
            }
        }
    }

    /// <summary>Tier 1 (UAX#29 grapheme) ids for a flat DB existence probe.</summary>
    public static void BuildBatchTier1Probe(
        IReadOnlyList<TierTree> trees,
        out List<Hash128> ids,
        out List<(int TreeIndex, int NodeIndex)> placements)
    {
        ids = new List<Hash128>();
        placements = new List<(int, int)>();
        for (int t = 0; t < trees.Count; t++)
        {
            var tree = trees[t];
            int n = tree.NodeCount;
            for (int j = 0; j < n; j++)
            {
                if (tree.GetNode((uint)j).Tier != 1) continue;
                placements.Add((t, j));
                ids.Add(tree.GetNode((uint)j).Id);
            }
        }
    }

    /// <summary>
    /// Tier 0/1 node ids for a flat <see cref="ISubstrateReader.EntitiesExistBitmapAsync"/>
    /// probe — descent only covers tier&gt;=2 trunks; without these bits,
    /// <see cref="MerkleDedup.TrunkShortcircuit"/> still treats unmarked ancestors as novel.
    /// Prefer <see cref="BuildBatchTier0PerfcachePresent"/> + <see cref="BuildBatchTier1Probe"/>.
    /// </summary>
    [Obsolete("Use BuildBatchTier0PerfcachePresent + BuildBatchTier1Probe")]
    public static void BuildBatchTier01Probe(
        IReadOnlyList<TierTree> trees,
        out List<Hash128> ids,
        out List<(int TreeIndex, int NodeIndex)> placements)
    {
        ids = new List<Hash128>();
        placements = new List<(int, int)>();
        for (int t = 0; t < trees.Count; t++)
        {
            var tree = trees[t];
            int n = tree.NodeCount;
            for (int j = 0; j < n; j++)
            {
                if (tree.GetNode((uint)j).Tier >= 2) continue;
                placements.Add((t, j));
                ids.Add(tree.GetNode((uint)j).Id);
            }
        }
    }

    /// <summary>
    /// OR tier 0/1 flat-probe hits from a merged batch bitmap into per-tree emit bitmaps.
    /// </summary>
    public static void ApplyBatchTier01Present(
        byte[][] perTreeEmitBm, IReadOnlyList<(int TreeIndex, int NodeIndex)> placements, byte[] flatBm)
    {
        long bits = (long)flatBm.Length * 8;
        for (int k = 0; k < placements.Count; k++)
        {
            if (k >= bits || (flatBm[k >> 3] & (1 << (k & 7))) == 0) continue;
            var (t, j) = placements[k];
            perTreeEmitBm[t][j >> 3] |= (byte)(1 << (j & 7));
        }
    }

    /// <summary>
    /// One <c>content_descent_bitmap</c> (+ optional tier01 flat) round-trip for many trees.
    /// Output aligns with <paramref name="trees"/>; null entries when the input tree is null.
    /// </summary>
    public static async Task<byte[]?[]> ProbeBatchEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader, CancellationToken ct = default)
    {
        int n = trees.Count;
        var results = new byte[]?[n];
        var probeTrees = new List<TierTree>(n);
        var probeIndices = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (trees[i] is null)
            {
                results[i] = null;
                continue;
            }
            probeIndices.Add(i);
            probeTrees.Add(trees[i]!);
        }

        if (probeTrees.Count == 0) return results;

        BuildBatchProbe(probeTrees, out var ids, out var parents, out var treeIdxToFlat);
        byte[]? descentBm = ids.Count > 0
            ? await reader.ContentDescentBitmapAsync(ids, parents, ct).ConfigureAwait(false)
            : null;

        var perTreeBm = new byte[probeTrees.Count][];
        for (int t = 0; t < probeTrees.Count; t++)
        {
            var tree = probeTrees[t];
            perTreeBm[t] = descentBm is null
                ? new byte[(tree.NodeCount + 7) / 8]
                : NodeEmitBitmap(tree, descentBm, treeIdxToFlat[t]);
        }

        BuildBatchTier0PerfcachePresent(probeTrees, perTreeBm, out var unresolvedTier0);

        BuildBatchTier1Probe(probeTrees, out var tier1Ids, out var tier1Placements);

        var dbIds = new List<Hash128>(tier1Ids.Count + unresolvedTier0.Count);
        var dbPlacements = new List<(int TreeIndex, int NodeIndex)>(tier1Placements.Count + unresolvedTier0.Count);
        dbPlacements.AddRange(tier1Placements);
        dbIds.AddRange(tier1Ids);
        foreach (var (t, j) in unresolvedTier0)
        {
            dbPlacements.Add((t, j));
            dbIds.Add(probeTrees[t].GetNode((uint)j).Id);
        }

        if (dbIds.Count > 0)
        {
            byte[] flat = await reader.EntitiesExistBitmapAsync(dbIds, ct).ConfigureAwait(false);
            ApplyBatchTier01Present(perTreeBm, dbPlacements, flat);
        }

        if (ids.Count > 0) reader.MarkProven(ids);

        for (int t = 0; t < probeTrees.Count; t++)
            results[probeIndices[t]] = perTreeBm[t];
        return results;
    }
}
