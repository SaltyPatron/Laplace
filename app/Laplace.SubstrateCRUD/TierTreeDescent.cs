using Laplace.Engine.Core;

namespace Laplace.SubstrateCRUD;

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

    public static byte[] NodeEmitBitmap(TierTree tree, byte[] descentBm, int[]? treeIdxToFlat = null)
    {
        int n = tree.NodeCount;
        var bm = new byte[(n + 7) / 8];
        long descentBits = (long)descentBm.Length * 8;

        if (treeIdxToFlat is null)
        {
            for (int j = 0; j < n; j++)
            {
                if (j < descentBits && (descentBm[j >> 3] & (1 << (j & 7))) != 0)
                    bm[j >> 3] |= (byte)(1 << (j & 7));
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

    public static void BuildBatchTier0Probe(
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
                if (tree.GetNode((uint)j).Tier != 0) continue;
                placements.Add((t, j));
                ids.Add(tree.GetNode((uint)j).Id);
            }
        }
    }

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

        if (ids.Count > 0) reader.MarkProven(ids);

        BuildBatchTier0Probe(probeTrees, out var tier0Ids, out var tier0Placements);
        if (tier0Ids.Count > 0)
        {
            byte[] tier0Bm = await reader.EntitiesExistBitmapAsync(tier0Ids, ct).ConfigureAwait(false);
            ApplyBatchTier01Present(perTreeBm, tier0Placements, tier0Bm);
        }

        BuildBatchTier1Probe(probeTrees, out var tier1Ids, out var tier1Placements);
        if (tier1Ids.Count > 0)
        {
            byte[] tier1Bm = await reader.EntitiesExistBitmapAsync(tier1Ids, ct).ConfigureAwait(false);
            ApplyBatchTier01Present(perTreeBm, tier1Placements, tier1Bm);
        }

        for (int t = 0; t < probeTrees.Count; t++)
            results[probeIndices[t]] = perTreeBm[t];
        return results;
    }
}
