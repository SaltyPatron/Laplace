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
        for (int k = 0; k < nodeIndices.Count; k++)
        {
            if (!BitmapBits.IsSet(flatBm, k)) continue;
            int j = nodeIndices[k];
            BitmapBits.Set(emitBm, j);
        }
    }

    public static byte[] NodeEmitBitmap(TierTree tree, byte[] descentBm, int[]? treeIdxToFlat = null)
    {
        int n = tree.NodeCount;
        var bm = new byte[BitmapBits.ByteLength(n)];

        if (treeIdxToFlat is null)
        {
            for (int j = 0; j < n; j++)
            {
                if (BitmapBits.IsSet(descentBm, j))
                    BitmapBits.Set(bm, j);
            }
        }
        else
        {
            for (int j = 0; j < n; j++)
            {
                int flat = treeIdxToFlat[j];
                if (flat < 0) continue;
                if (BitmapBits.IsSet(descentBm, flat))
                    BitmapBits.Set(bm, j);
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
                    BitmapBits.Set(perTreeEmitBm[t], j);
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
        for (int k = 0; k < placements.Count; k++)
        {
            if (!BitmapBits.IsSet(flatBm, k)) continue;
            var (t, j) = placements[k];
            BitmapBits.Set(perTreeEmitBm[t], j);
        }
    }

    /// <summary>
    /// Uniform tier-by-tier batch existence probing. Each round deduplicates exact
    /// candidate IDs across all trees and checks them in one bounded reader call.
    /// A stored parent does not prove its children were committed: parallel entity
    /// COPY transactions can leave a partial tree after failure. Only each node's
    /// own positive probe/cache answer suppresses that entity's insertion. Raw
    /// physicality observations remain independent of this entity bitmap.
    /// </summary>
    public static Task<byte[]?[]> ProbeBatchEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader, CancellationToken ct = default)
        => ProbeBatchEmitBitmapsAsync(trees, reader, probedAbsent: null, ct);

    /// <summary>
    /// Working-set overload. <paramref name="probedAbsent"/> is a
    /// caller-owned, working-set-lifetime record of ids a previous round in
    /// the SAME working set already probed and found absent: they are
    /// skipped (bit left 0 — "emit") without re-querying, because the
    /// working set's stage witness-dedup already absorbed their first
    /// emission and re-probing an id that cannot have appeared since our
    /// own unwritten working set began is pure waste. This is an efficiency
    /// cache only — it must never be shared across working sets or
    /// processes (another writer may commit the id at any time; the write
    /// protocol's in-transaction re-probe is what restores correctness at
    /// the boundary). Ids proven PRESENT are handled by the reader's own
    /// process-lifetime proven cache and are confirmed here without a DB
    /// round trip, handled exactly as a fresh confirmation of that node would be.
    /// </summary>
    public static async Task<byte[]?[]> ProbeBatchEmitBitmapsAsync(
        IReadOnlyList<TierTree?> trees, ISubstrateReader reader,
        ISet<Hash128>? probedAbsent, CancellationToken ct = default)
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

        int treeCount = probeTrees.Count;
        var perTreeBm = new byte[treeCount][];
        // A node is resolved only by its own exact identity answer. Parent
        // presence carries no durable statement about descendants.
        var resolved = new bool[treeCount][];
        int maxTier = 0;
        for (int t = 0; t < treeCount; t++)
        {
            int nodeCount = probeTrees[t].NodeCount;
            perTreeBm[t] = new byte[BitmapBits.ByteLength(nodeCount)];
            resolved[t] = new bool[nodeCount];
            for (int j = 0; j < nodeCount; j++)
                maxTier = Math.Max(maxTier, probeTrees[t].GetNode((uint)j).Tier);
        }

        for (int tier = maxTier; tier >= 0; tier--)
        {
            // One probe slot per DISTINCT id this round; every (tree, node)
            // occurrence of that id shares the slot's answer. OMW-style
            // sources repeat the same lemma across thousands of records --
            // probing per occurrence multiplies the round's row count for
            // no information.
            var ids = new List<Hash128>();
            var slotOf = new Dictionary<Hash128, int>();
            var placements = new List<List<(int TreeIndex, int NodeIndex)>>();
            for (int t = 0; t < treeCount; t++)
            {
                var tree = probeTrees[t];
                int nodeCount = tree.NodeCount;
                for (int j = 0; j < nodeCount; j++)
                {
                    if (resolved[t][j]) continue;
                    if (tree.GetNode((uint)j).Tier != tier) continue;
                    var id = tree.GetNode((uint)j).Id;

                    // An exact positive cache hit suppresses only this entity;
                    // descendants retain their own probe/cache decisions.
                    if (reader.IsProvenPresent(id))
                    {
                        BitmapBits.Set(perTreeBm[t], j);
                        resolved[t][j] = true;
                        continue;
                    }

                    // Already probed absent within this working set: leave
                    // the bit 0 (emit); stage witness-dedup absorbs the
                    // duplicate emission. Children still probe normally.
                    if (probedAbsent is not null && probedAbsent.Contains(id))
                    {
                        resolved[t][j] = true;
                        continue;
                    }

                    if (!slotOf.TryGetValue(id, out int slot))
                    {
                        slot = ids.Count;
                        slotOf[id] = slot;
                        ids.Add(id);
                        placements.Add(new List<(int, int)>(1));
                    }
                    placements[slot].Add((t, j));
                }
            }
            if (ids.Count == 0) continue;

            var presenceScope = reader.CapturePresenceScope();
            byte[] bm = await reader.TierBatchExistenceProbeAsync(ids, (short)tier, ct).ConfigureAwait(false);

            var confirmedPresent = new List<Hash128>();
            for (int k = 0; k < ids.Count; k++)
            {
                bool present = BitmapBits.IsSet(bm, k);
                if (!present)
                {
                    probedAbsent?.Add(ids[k]);
                    continue;
                }
                confirmedPresent.Add(ids[k]);
                foreach (var (t, j) in placements[k])
                {
                    BitmapBits.Set(perTreeBm[t], j);
                    resolved[t][j] = true;
                }
            }

            // Only the ids THIS round's real query positively confirmed
            // present are ever marked proven -- never the round's whole,
            // unfiltered candidate list (that was the bug).
            if (confirmedPresent.Count > 0) reader.MarkProven(confirmedPresent, presenceScope);
        }

        for (int t = 0; t < treeCount; t++)
            results[probeIndices[t]] = perTreeBm[t];
        return results;
    }

}
