using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class IngestParallelismTests
{
    [Fact]
    public void ResolveFileWorkers_ScalesToPhysicalPCoresMinusHeadroom()
    {
        int[] pPrimary = [0, 2, 4, 6, 8, 10, 12, 14];
        CpuTopology.TestOverride = new CpuTopology.CpuSnapshot(8, 16, 32, IsHybrid: true);
        CpuTopology.TestPCoreIndicesOverride = pPrimary;
        CpuTopology.TestPoolsOverride = new CpuTopology.TopologyPools(
            isHybrid: true, physicalPCores: 8, physicalECores: 16, logicalCount: 32,
            primaryPLogicalCount: 16,
            primaryPCoreGlobalIndices: pPrimary,
            primaryPCoreCpuSetIds: pPrimary.Select(i => (uint)i).ToArray(),
            primaryPCoreAffinities: pPrimary.Select(i => new CpuTopology.ProcessorAffinity(0, 1UL << i)).ToArray(),
            efficientCoreGlobalIndices: Enumerable.Range(16, 16).ToArray(),
            efficientCoreCpuSetIds: [],
            efficientCoreAffinities: [],
            source: "test");
        try
        {
            Assert.Equal(6, IngestParallelism.ResolveFileWorkers(coreHeadroom: 2));
            Assert.Equal(4, IngestParallelism.ResolveFileWorkers(coreHeadroom: 4));
        }
        finally
        {
            CpuTopology.TestPoolsOverride = null;
            CpuTopology.TestPCoreIndicesOverride = null;
            CpuTopology.TestOverride = null;
        }
    }

    [Fact]
    public void ResolveFileWorkers_SingleCoreBox()
    {
        CpuTopology.TestOverride = new CpuTopology.CpuSnapshot(1, 0, 1, IsHybrid: false);
        CpuTopology.TestPoolsOverride = CpuTopology.TopologyPools.Uniform(1, "test-single");
        try
        {
            Assert.Equal(1, IngestParallelism.ResolveFileWorkers());
        }
        finally
        {
            CpuTopology.TestPoolsOverride = null;
            CpuTopology.TestOverride = null;
        }
    }
}
