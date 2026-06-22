using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public class CpuTopologyTests
{
    [Fact]
    public void ResolveCpuBoundWorkers_ClampsToPerformanceMinusHeadroom()
    {
        CpuTopology.TestOverride = new CpuTopology.CpuSnapshot(8, 16, 32, IsHybrid: true);
        try
        {
            Assert.Equal(7, CpuTopology.ResolveCpuBoundWorkers(headroom: 1, maxCap: 16));
            Assert.Equal(6, CpuTopology.ResolveCpuBoundWorkers(headroom: 2, maxCap: 16));
            Assert.Equal(1, CpuTopology.ResolveCpuBoundWorkers(headroom: 10, maxCap: 16));
            Assert.Equal(4, CpuTopology.ResolveCpuBoundWorkers(headroom: 1, maxCap: 4));
        }
        finally
        {
            CpuTopology.TestOverride = null;
        }
    }

    [Fact]
    public void ResolveIoBoundWorkers_CapsAtDefault()
    {
        CpuTopology.TestOverride = new CpuTopology.CpuSnapshot(8, 16, 32, IsHybrid: true);
        try
        {
            Assert.Equal(8, CpuTopology.ResolveIoBoundWorkers(defaultCap: 8));
            Assert.Equal(4, CpuTopology.ResolveIoBoundWorkers(defaultCap: 4));
        }
        finally
        {
            CpuTopology.TestOverride = null;
        }
    }

    [Fact]
    public void ResolveCpuBoundWorkers_EnvOverrideWins()
    {
        CpuTopology.TestOverride = new CpuTopology.CpuSnapshot(8, 16, 32, IsHybrid: true);
        string? prior = Environment.GetEnvironmentVariable("LAPLACE_CPU_BOUND_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_CPU_BOUND_WORKERS", "3");
            Assert.Equal(3, CpuTopology.ResolveCpuBoundWorkers(headroom: 1, maxCap: 16));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_CPU_BOUND_WORKERS", prior);
            CpuTopology.TestOverride = null;
        }
    }

    [Fact]
    public void Detect_FallbackSnapshotIsUsableOnCi()
    {
        CpuTopology.TestOverride = null;
        var snap = CpuTopology.Detect();
        Assert.True(snap.PerformanceCoreCount >= 1);
        Assert.True(snap.LogicalProcessorCount >= 1);
        Assert.Equal(Environment.ProcessorCount, snap.LogicalProcessorCount);
    }
}
