using Laplace.Engine.Core;

using Xunit;



namespace Laplace.Engine.Core.Tests;



public class CpuTopologyTests

{

    private static void SetHybrid14900KLikeTopology()

    {

        int[] pPrimary = [0, 2, 4, 6, 8, 10, 12, 14];

        int[] eLps = Enumerable.Range(16, 16).ToArray();



        CpuTopology.TestOverride = new CpuTopology.CpuSnapshot(8, 16, 32, IsHybrid: true);

        CpuTopology.TestPCoreIndicesOverride = pPrimary;

        CpuTopology.TestECoreIndicesOverride = eLps;

        CpuTopology.TestPoolsOverride = new CpuTopology.TopologyPools(

            isHybrid: true,

            physicalPCores: 8,

            physicalECores: 16,

            logicalCount: 32,

            primaryPLogicalCount: 16,

            primaryPCoreGlobalIndices: pPrimary,

            primaryPCoreCpuSetIds: pPrimary.Select(i => (uint)i).ToArray(),

            primaryPCoreAffinities: pPrimary.Select(i => new CpuTopology.ProcessorAffinity(0, 1UL << i)).ToArray(),

            efficientCoreGlobalIndices: eLps,

            efficientCoreCpuSetIds: eLps.Select(i => (uint)i).ToArray(),

            efficientCoreAffinities: eLps.Select(i => new CpuTopology.ProcessorAffinity(0, 1UL << i)).ToArray(),

            source: "test-14900ks");

    }



    private static void ClearTestOverrides()

    {

        CpuTopology.TestPoolsOverride = null;

        CpuTopology.TestPCoreIndicesOverride = null;

        CpuTopology.TestECoreIndicesOverride = null;

        CpuTopology.TestOverride = null;

    }



    [Fact]

    public void ResolveCpuBoundWorkers_UsesPhysicalPCoresMinusHeadroom()

    {

        SetHybrid14900KLikeTopology();

        try

        {

            Assert.Equal(7, CpuTopology.ResolveCpuBoundWorkers(headroom: 1));

            Assert.Equal(6, CpuTopology.ResolveCpuBoundWorkers(headroom: 2));

            Assert.Equal(1, CpuTopology.ResolveCpuBoundWorkers(headroom: 20));

            Assert.Equal(4, CpuTopology.ResolveCpuBoundWorkers(headroom: 1, maxCap: 4));

        }

        finally { ClearTestOverrides(); }

    }



    [Fact]

    public void ResolveIngestCommitWorkers_UsesECorePoolMinusHeadroom()

    {

        SetHybrid14900KLikeTopology();

        try

        {

            Assert.Equal(15, CpuTopology.ResolveIngestCommitWorkers(headroom: 1));

            Assert.Equal(1, CpuTopology.ResolveIngestCommitWorkers(headroom: 20));

        }

        finally { ClearTestOverrides(); }

    }



    [Fact]

    public void ResolveApplyPartitions_MatchesPhysicalPCoreCount()

    {

        SetHybrid14900KLikeTopology();

        try

        {

            Assert.Equal(8, CpuTopology.ResolveApplyPartitions());

        }

        finally { ClearTestOverrides(); }

    }



    [Fact]

    public void PrimaryPCoreIndices_OnePerPhysicalCore_NotHtSiblings()

    {

        SetHybrid14900KLikeTopology();

        try

        {

            var idx = CpuTopology.PerformanceCoreCpuIndices;

            Assert.Equal(8, idx.Count);

            Assert.Equal(24, idx.Count + CpuTopology.EfficientCoreCpuIndices.Count);

            Assert.All(idx, i => Assert.True(i < 16));

            Assert.Equal([0, 2, 4, 6, 8, 10, 12, 14], idx);

        }

        finally { ClearTestOverrides(); }

    }



    [Fact]

    public void ResolveIngestCommitWorkers_SingleCoreBox()

    {

        CpuTopology.TestOverride = new CpuTopology.CpuSnapshot(1, 0, 1, IsHybrid: false);

        CpuTopology.TestPoolsOverride = CpuTopology.TopologyPools.Uniform(1, "test-single");

        try

        {

            Assert.Equal(1, CpuTopology.ResolveIngestCommitWorkers(headroom: 1));

        }

        finally { ClearTestOverrides(); }

    }



    [Fact]

    public void ParseCpuList_ExpandsRanges()

    {

        var parsed = CpuTopology.ParseCpuList("0-3,16,18-19");

        Assert.Equal([0, 1, 2, 3, 16, 18, 19], parsed);

    }



    [Fact]

    public void Detect_FallbackSnapshotIsUsableOnCi()

    {

        ClearTestOverrides();

        var snap = CpuTopology.Detect();

        Assert.True(snap.PerformanceCoreCount >= 1);

        Assert.True(snap.LogicalProcessorCount >= 1);

        // Detect() reports the machine's REAL topology (hybrid-aware, via sysfs).
        // On a hybrid CPU under a cgroup/affinity cap it legitimately exceeds the
        // process-visible Environment.ProcessorCount — e.g. a 12-core quota on a
        // 32-thread hybrid box (14900KS: 8 P + 16 E) gives Detect()=32 but
        // ProcessorCount=12. So do NOT assert equality with the process count (a
        // false invariant across machines); assert usable + internally consistent.
        Assert.True(snap.LogicalProcessorCount >= snap.PerformanceCoreCount);

    }

}


