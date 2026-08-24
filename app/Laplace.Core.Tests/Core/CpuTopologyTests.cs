using System;
using System.Linq;
using System.IO;
using Laplace.Engine.Core;

using Xunit;



namespace Laplace.Engine.Core.Tests;

[CollectionDefinition("CpuTopology", DisableParallelization = true)]
public sealed class CpuTopologyTestCollection { }

[Collection("CpuTopology")]
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

    public void ResolveCpuBoundWorkers_UsesFullPhysicalPCorePool()

    {

        SetHybrid14900KLikeTopology();

        try

        {

            Assert.Equal(8, CpuTopology.ResolveCpuBoundWorkers());

        }

        finally { ClearTestOverrides(); }

    }



    [Fact]

    public void ResolveIngestCommitWorkers_UsesFullECorePool()

    {

        SetHybrid14900KLikeTopology();

        try

        {

            Assert.Equal(16, CpuTopology.ResolveIngestCommitWorkers());

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

            Assert.Equal(1, CpuTopology.ResolveIngestCommitWorkers());

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

    // GH #986. TryDetectLinuxSysfsPools keys on /sys/devices/cpu_core/cpus, which the
    // kernel publishes only for hybrid P/E parts -- it was written against a 14900KS. On a
    // non-hybrid CPU that file is absent, the detector returns false SILENTLY (the catch
    // only fires on an exception), and detection fell through to
    // Uniform(Environment.ProcessorCount): SMT threads reported as physical cores, with
    // every ingest pool sized from a doubled base. Measured live on hart-server, an
    // i7-6850K with 6 cores / 12 threads: p_physical=12.
    [Fact]
    public void NonHybridLinux_ReportsPhysicalCores_NotSmtThreads()
    {
        // Not a skip framework here: on a non-Linux host there is nothing to assert and
        // the detector is not reachable, so the test is vacuously satisfied.
        if (!OperatingSystem.IsLinux() || !File.Exists("/sys/devices/system/cpu/present")) return;

        Assert.True(CpuTopology.TryDetectLinuxGenericSysfsPools(out var pools),
            "generic Linux topology detection must succeed wherever sysfs cpu topology exists");
        Assert.NotNull(pools);
        Assert.False(pools!.IsHybrid);
        Assert.True(pools.PhysicalPCores >= 1);

        // A physical core is never MORE numerous than the logical processors it hosts.
        // The old fallback reported exactly logicalCount, which is the bug.
        Assert.True(pools.PhysicalPCores <= pools.LogicalCount,
            $"physical {pools.PhysicalPCores} exceeds logical {pools.LogicalCount}");

        // Every primary index must be a CPU this process may actually run on.
        var allowed = new HashSet<int>(CpuTopology.ReadLinuxAllowedCpus());
        foreach (int i in pools.PrimaryPCoreGlobalIndices)
            Assert.Contains(i, allowed);
    }

    // sysfs describes the HOST. A container under a cpuset still sees every host CPU in
    // /sys/devices/system/cpu/present, so sizing from `present` would give a 2-CPU
    // container the pools of a 64-core machine. Cpus_allowed_list is what the process may
    // actually run on, and it is what the detector reads.
    [Fact]
    public void AllowedCpus_ComeFromTheProcessAffinityMask()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/status")) return;

        var allowed = CpuTopology.ReadLinuxAllowedCpus();
        Assert.NotEmpty(allowed);
        Assert.Equal(allowed.Length, allowed.Distinct().Count());
        Assert.Equal(allowed.OrderBy(x => x), allowed);
    }
}
