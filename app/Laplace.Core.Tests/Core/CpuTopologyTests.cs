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

        // Detect() reads host topology from sysfs, which can exceed the process-visible
        // Environment.ProcessorCount under a cgroup or affinity cap, so only internal
        // consistency is asserted.
        Assert.True(snap.LogicalProcessorCount >= snap.PerformanceCoreCount);

    }

    // On a non-hybrid Linux CPU (no /sys/devices/cpu_core/cpus) the generic sysfs
    // detector still succeeds and counts physical cores, not SMT threads.
    [Fact]
    public void NonHybridLinux_ReportsPhysicalCores_NotSmtThreads()
    {
        // Vacuous off Linux: the detector is unreachable there.
        if (!OperatingSystem.IsLinux() || !File.Exists("/sys/devices/system/cpu/present")) return;

        Assert.True(CpuTopology.TryDetectLinuxGenericSysfsPools(out var pools),
            "generic Linux topology detection must succeed wherever sysfs cpu topology exists");
        Assert.NotNull(pools);
        Assert.False(pools!.IsHybrid);
        Assert.True(pools.PhysicalPCores >= 1);

        // Physical cores never outnumber the logical processors they host.
        Assert.True(pools.PhysicalPCores <= pools.LogicalCount,
            $"physical {pools.PhysicalPCores} exceeds logical {pools.LogicalCount}");

        // Every primary index must be a CPU this process may actually run on.
        var allowed = new HashSet<int>(CpuTopology.ReadLinuxAllowedCpus());
        foreach (int i in pools.PrimaryPCoreGlobalIndices)
            Assert.Contains(i, allowed);
    }

    // The detector reads Cpus_allowed_list (what this process may run on), not sysfs
    // `present`, which lists every host CPU even inside a cpuset-limited container.
    [Fact]
    public void AllowedCpus_ComeFromTheProcessAffinityMask()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/status")) return;

        var allowed = CpuTopology.ReadLinuxAllowedCpus();
        Assert.NotEmpty(allowed);
        Assert.Equal(allowed.Length, allowed.Distinct().Count());
        Assert.Equal(allowed.OrderBy(x => x), allowed);
    }

    // The reported CpuTopology.PerformanceCoreCount, from which every pool is sized, is
    // strictly below LogicalProcessorCount whenever sysfs shows cpu0 with SMT siblings.
    [Fact]
    public void ReportedPhysicalCores_AreCoresNotThreads()
    {
        if (!OperatingSystem.IsLinux()) return;
        const string siblings = "/sys/devices/system/cpu/cpu0/topology/thread_siblings_list";
        if (!File.Exists(siblings)) return;

        // "0,6" or "0-1" -- more than one entry means SMT is on for this core.
        string list = File.ReadAllText(siblings).Trim();
        if (!list.Contains(',') && !list.Contains('-')) return;

        int physical = CpuTopology.PerformanceCoreCount;
        int logical = CpuTopology.LogicalProcessorCount;

        Assert.True(physical > 0);
        Assert.True(physical < logical,
            $"SMT is enabled (cpu0 siblings '{list}') so physical cores must be fewer than "
            + $"logical processors; got physical={physical} logical={logical}. Equal means the "
            + "reported count is threads, which is GH #986.");
    }
}
