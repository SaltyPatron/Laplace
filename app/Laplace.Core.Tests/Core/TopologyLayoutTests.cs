using System;
using System.Linq;
using Xunit;

namespace Laplace.Engine.Core.Tests;

/// <summary>
/// GH #986 was verified on exactly ONE machine: a non-hybrid i7-6850K, 6 cores / 12 threads.
/// The hybrid path -- which is what the original Linux detector was written around, keying on
/// /sys/devices/cpu_core/cpus that only hybrid P/E parts publish -- and dual-socket and
/// no-SMT server parts were untested by anything.
///
/// "No physical ARM box" does not mean nothing can be tested. The layouts themselves are
/// data, and CpuTopology.TestPoolsOverride takes them, so DetectPlatform's rule can be
/// exercised against every shape the detector can produce.
///
/// The rule under test (CpuTopology.DetectPlatform): trust pools.PhysicalPCores when
/// detection produced a real reading. It previously discarded that value for any NON-hybrid
/// CPU and returned Environment.ProcessorCount -- the LOGICAL count -- as the physical one,
/// which is why 911 tests passed at p_physical=12 on a 6-core part.
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

    // i7-6850K, the machine #986 was found on: 6 cores, SMT, 12 logical, no E-cores.
    [Fact]
    public void NonHybridSmt_ReportsCoresNotThreads()
    {
        CpuTopology.TestPoolsOverride = Pools(false, 6, 0, 12, 12, "linux-sysfs-generic");
        Assert.Equal(6, CpuTopology.PerformanceCoreCount);
        Assert.Equal(0, CpuTopology.EfficientCoreCount);
        Assert.False(CpuTopology.IsHybrid);
    }

    // 14900KS: 8 P-cores with SMT (16 threads) + 16 E-cores without = 32 logical. This is the
    // layout the original detector was written for and the only one it handled.
    [Fact]
    public void HybridPeCores_KeepBothPoolsDistinct()
    {
        CpuTopology.TestPoolsOverride = Pools(true, 8, 16, 32, 16, "linux-sysfs");
        Assert.Equal(8, CpuTopology.PerformanceCoreCount);
        Assert.Equal(16, CpuTopology.EfficientCoreCount);
        Assert.True(CpuTopology.IsHybrid);

        // E-cores must never be counted as P-cores: applyPartitions is sized from the P pool
        // and a sustained-AVX fold scheduled onto an E-core is not equivalent work.
        Assert.NotEqual(CpuTopology.PerformanceCoreCount, CpuTopology.LogicalProcessorCount);
    }

    // Dual-socket EPYC-shaped: 2 packages x 32 cores, SMT, 128 logical. core_id restarts per
    // socket, so a dedupe keyed on core_id alone HALVES this to 32.
    [Fact]
    public void DualSocket_CountsBothPackages()
    {
        CpuTopology.TestPoolsOverride = Pools(false, 64, 0, 128, 128, "linux-sysfs-generic");
        Assert.Equal(64, CpuTopology.PerformanceCoreCount);
        Assert.Equal(128, CpuTopology.LogicalProcessorCount);
    }

    // ARM server part: no SMT at all, so physical == logical and nothing is doubled.
    [Fact]
    public void NoSmt_PhysicalEqualsLogical()
    {
        CpuTopology.TestPoolsOverride = Pools(false, 16, 0, 16, 16, "linux-sysfs-generic");
        Assert.Equal(16, CpuTopology.PerformanceCoreCount);
        Assert.Equal(16, CpuTopology.LogicalProcessorCount);
    }

    // A container under a cpuset sees the HOST's cpus in sysfs. The detector reads
    // /proc/self/status Cpus_allowed_list instead, so the pools carry what the process may
    // actually run on -- 2 cores of a 64-core host, not 64.
    [Fact]
    public void CpusetRestricted_SizesToTheProcessNotTheHost()
    {
        CpuTopology.TestPoolsOverride = Pools(false, 2, 0, 4, 4, "linux-sysfs-generic");
        Assert.Equal(2, CpuTopology.PerformanceCoreCount);
        Assert.True(CpuTopology.PerformanceCoreCount < 64,
            "a cpuset-restricted container must not be sized for the host");
    }

    // Every derived pool follows PerformanceCoreCount. This is the relationship that nothing
    // asserted, which is why a 2x error in the base was invisible to 911 tests.
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
