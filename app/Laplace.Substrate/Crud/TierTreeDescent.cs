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

    /// <summary>
    /// Uniform tier-by-tier, trunk-to-leaf, breadth-first batch existence
    /// probe, applied identically to every tier (including tier 0 -- no
    /// special-casing). For each tier, from the highest tier present in the
    /// batch down to 0:
    ///   1. Collect every not-yet-resolved node at that tier, across every
    ///      tree in the batch, into one flat candidate list.
    ///   2. Batch-check all of them in ONE round-trip
    ///      (<see cref="ISubstrateReader.TierBatchExistenceProbeAsync"/>).
    ///   3. For every id the round's bitmap actually confirmed present,
    ///      mark its emit-bit AND mark its whole subtree "resolved" --
    ///      children of a confirmed-present node are guaranteed present too
    ///      by the content-addressing invariant (same content => same hash;
    ///      an atomically-committed parent implies every committed child),
    ///      so they are never enumerated, never queried, and never
    ///      probed again in a later round.
    ///
    /// On a fresh DB this naturally degenerates to "everything absent, full
    /// descent" -- the same total cost as an unconditional insert. On a DB
    /// that already has the content, the top-tier (root) check short-
    /// circuits almost immediately. This is the actual efficiency win, not
    /// skipping checks for any particular tier.
    ///
    /// This replaces the previous scheme, which ran exactly one flat probe
    /// covering every tier at once (no short-circuiting at all, so no
    /// efficiency win from content-addressing) and then called
    /// reader.MarkProven() on the ENTIRE unfiltered candidate list --
    /// including ids that same call's own bitmap had just proven absent.
    /// That unconditional MarkProven() call was a real, live-reproduced
    /// cache self-poisoning bug (see dorian.txt repro in
    /// .scratchpad/02_Identified_Issues.txt): NpgsqlSubstrateReader's
    /// process-lifetime `_proven` cache would then falsely report those
    /// ids as existing for the rest of the ingest run, so a common
    /// grapheme/word's real entities row would never actually get written,
    /// while its parent's trajectory kept referencing that id anyway. Here,
    /// MarkProven is only ever called with the subset of a round's
    /// candidates that round's own bitmap positively confirmed present.
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
    /// round trip, subtree-pruned exactly as a fresh confirmation would be.
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
        // resolved[t][j]: node j of tree t either was itself confirmed
        // present, or is a descendant of a node confirmed present in an
        // earlier (higher-tier) round -- either way its subtree is fully
        // covered by the content-addressing guarantee and must never be
        // enumerated as a probe candidate again.
        var resolved = new bool[treeCount][];
        int maxTier = 0;
        for (int t = 0; t < treeCount; t++)
        {
            int nodeCount = probeTrees[t].NodeCount;
            perTreeBm[t] = new byte[(nodeCount + 7) / 8];
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

                    // Already proven present (process-lifetime cache, only
                    // ever populated from real positive results): confirm
                    // and subtree-prune without a DB round trip.
                    if (reader.IsProvenPresent(id))
                    {
                        perTreeBm[t][j >> 3] |= (byte)(1 << (j & 7));
                        resolved[t][j] = true;
                        MarkSubtreeResolvedPresent(probeTrees[t], perTreeBm[t], resolved[t], (uint)j);
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

            byte[] bm = await reader.TierBatchExistenceProbeAsync(ids, (short)tier, ct).ConfigureAwait(false);
            long bits = (long)bm.Length * 8;

            var confirmedPresent = new List<Hash128>();
            for (int k = 0; k < ids.Count; k++)
            {
                bool present = k < bits && (bm[k >> 3] & (1 << (k & 7))) != 0;
                if (!present)
                {
                    probedAbsent?.Add(ids[k]);
                    continue;
                }
                confirmedPresent.Add(ids[k]);
                foreach (var (t, j) in placements[k])
                {
                    perTreeBm[t][j >> 3] |= (byte)(1 << (j & 7));
                    resolved[t][j] = true;
                    MarkSubtreeResolvedPresent(probeTrees[t], perTreeBm[t], resolved[t], (uint)j);
                }
            }

            // Only the ids THIS round's real query positively confirmed
            // present are ever marked proven -- never the round's whole,
            // unfiltered candidate list (that was the bug).
            if (confirmedPresent.Count > 0) reader.MarkProven(confirmedPresent);
        }

        for (int t = 0; t < treeCount; t++)
            results[probeIndices[t]] = perTreeBm[t];
        return results;
    }

    /// <summary>
    /// Marks every descendant of <paramref name="nodeIdx"/> (a node just
    /// confirmed present) as resolved+present, without querying or
    /// enumerating them individually. Uses the tree's own contiguous
    /// child-range storage (FirstChildIdx/ChildCount) rather than a
    /// parent-pointer search, so each node is visited by exactly one
    /// covering walk for the whole probe -- total cost is O(subtree size),
    /// and O(n) summed across every call for one tree, not O(n^2).
    /// </summary>
    private static void MarkSubtreeResolvedPresent(TierTree tree, byte[] emitBm, bool[] resolved, uint nodeIdx)
    {
        var stack = new Stack<uint>();
        stack.Push(nodeIdx);
        while (stack.Count > 0)
        {
            uint idx = stack.Pop();
            var node = tree.GetNode(idx);
            for (uint c = 0; c < node.ChildCount; c++)
            {
                uint childIdx = node.FirstChildIdx + c;
                if (resolved[childIdx]) continue;
                resolved[childIdx] = true;
                emitBm[childIdx >> 3] |= (byte)(1 << (int)(childIdx & 7));
                stack.Push(childIdx);
            }
        }
    }
}
