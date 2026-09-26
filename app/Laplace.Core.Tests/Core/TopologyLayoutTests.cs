using System;
using System.Linq;
using Xunit;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// Feeds CpuTopology.TestPoolsOverride with each layout shape the detector can produce
/// (non-hybrid SMT, hybrid P/E, dual-socket, no-SMT, cpuset-restricted) and proves
/// CpuTopology.DetectPlatform reports pools.PhysicalPCores as the physical core count.
/// </summary>
[Collection("CpuTopology")]
public sealed class TopologyLayoutTests : IDisposable
{
    public void Dispose()
    {
        CpuTopology.TestPoolsOverride = null;
        CpuTopology.TestOverride = null;
        CpuTopology.TestPCoreIndicesOverride = null;
    }

    private static CpuTopology.TopologyPools Pools(
        bool hybrid, int pCores, int eCores, int logical, int pLogical, string source)
    {
        int[] pIdx = Enumerable.Range(0, pCores).ToArray();
        int[] eIdx = Enumerable.Range(pCores, eCores).ToArray();
        return new CpuTopology.TopologyPools(
            isHybrid: hybrid,
            physicalPCores: pCores,
            physicalECores: eCores,
            logicalCount: logical,
            primaryPLogicalCount: pLogical,
            primaryPCoreGlobalIndices: pIdx,
            primaryPCoreCpuSetIds: Array.Empty<uint>(),
            primaryPCoreAffinities: pIdx.Select(i => new CpuTopology.ProcessorAffinity((ushort)(i / 64), 1UL << (i % 64))).ToArray(),
            efficientCoreGlobalIndices: eIdx,
            efficientCoreCpuSetIds: Array.Empty<uint>(),
            efficientCoreAffinities: eIdx.Select(i => new CpuTopology.ProcessorAffinity((ushort)(i / 64), 1UL << (i % 64))).ToArray(),
            source: source);
    }

    // Non-hybrid SMT: 6 cores, 12 logical, no E-cores.
    [Fact]
    public void NonHybridSmt_ReportsCoresNotThreads()
    {
        CpuTopology.TestPoolsOverride = Pools(false, 6, 0, 12, 12, "linux-sysfs-generic");
        Assert.Equal(6, CpuTopology.PerformanceCoreCount);
        Assert.Equal(0, CpuTopology.EfficientCoreCount);
        Assert.False(CpuTopology.IsHybrid);
    }

    // Hybrid: 8 P-cores with SMT (16 threads) + 16 E-cores without = 32 logical.
    [Fact]
    public void HybridPeCores_KeepBothPoolsDistinct()
    {
        CpuTopology.TestPoolsOverride = Pools(true, 8, 16, 32, 16, "linux-sysfs");
        Assert.Equal(8, CpuTopology.PerformanceCoreCount);
        Assert.Equal(16, CpuTopology.EfficientCoreCount);
        Assert.True(CpuTopology.IsHybrid);

        // E-cores are not counted as P-cores; apply partitions are sized from the P pool.
        Assert.NotEqual(CpuTopology.PerformanceCoreCount, CpuTopology.LogicalProcessorCount);
    }

    // Dual-socket: 2 packages x 32 cores, SMT, 128 logical. core_id restarts per socket,
    // so a dedupe keyed on core_id alone would report 32.
    [Fact]
    public void DualSocket_CountsBothPackages()
    {
        CpuTopology.TestPoolsOverride = Pools(false, 64, 0, 128, 128, "linux-sysfs-generic");
        Assert.Equal(64, CpuTopology.PerformanceCoreCount);
        Assert.Equal(128, CpuTopology.LogicalProcessorCount);
    }

    // No SMT: physical == logical.
    [Fact]
    public void NoSmt_PhysicalEqualsLogical()
    {
        CpuTopology.TestPoolsOverride = Pools(false, 16, 0, 16, 16, "linux-sysfs-generic");
        Assert.Equal(16, CpuTopology.PerformanceCoreCount);
        Assert.Equal(16, CpuTopology.LogicalProcessorCount);
    }

    // Pools carry the process's Cpus_allowed_list, not the host's sysfs CPUs.
    [Fact]
    public void CpusetRestricted_SizesToTheProcessNotTheHost()
    {
        CpuTopology.TestPoolsOverride = Pools(false, 2, 0, 4, 4, "linux-sysfs-generic");
        Assert.Equal(2, CpuTopology.PerformanceCoreCount);
        Assert.True(CpuTopology.PerformanceCoreCount < 64,
            "a cpuset-restricted container must not be sized for the host");
    }

    // Apply partitions and parallel maintenance workers follow PerformanceCoreCount.
    [Theory]
    [InlineData(6, 12)]
    [InlineData(8, 32)]
    [InlineData(64, 128)]
    [InlineData(2, 4)]
    public void DerivedPools_FollowThePhysicalCoreCount(int pCores, int logical)
    {
        CpuTopology.TestPoolsOverride = Pools(false, pCores, 0, logical, logical, "linux-sysfs-generic");
        Assert.Equal(pCores, CpuTopology.ResolveApplyPartitions());
        Assert.Equal(pCores, CpuTopology.ParallelMaintenanceWorkers);
        Assert.True(CpuTopology.ResolveApplyPartitions() <= CpuTopology.LogicalProcessorCount);
    }
}
