using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class IngestParallelismTests
{
    [Fact]
    public void ResolveFileWorkers_UnsetEnv_ScalesToPerformanceCoreMinusHeadroom()
    {
        CpuTopology.TestOverride = new CpuTopology.CpuSnapshot(8, 16, 32, IsHybrid: true);
        string? prior = Environment.GetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS", null);
            Assert.Equal(6, IngestParallelism.ResolveFileWorkers(coreHeadroom: 2));
            Assert.Equal(4, IngestParallelism.ResolveFileWorkers(coreHeadroom: 4));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS", prior);
            CpuTopology.TestOverride = null;
        }
    }

    [Fact]
    public void ResolveFileWorkers_ExplicitEnv_Wins()
    {
        CpuTopology.TestOverride = new CpuTopology.CpuSnapshot(8, 16, 32, IsHybrid: true);
        string? prior = Environment.GetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS", "3");
            Assert.Equal(3, IngestParallelism.ResolveFileWorkers());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS", prior);
            CpuTopology.TestOverride = null;
        }
    }

    [Fact]
    public void ResolveFileWorkers_UserSerialOverride_IsHonored()
    {
        CpuTopology.TestOverride = new CpuTopology.CpuSnapshot(8, 16, 32, IsHybrid: true);
        string? prior = Environment.GetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS", "1");
            Assert.Equal(1, IngestParallelism.ResolveFileWorkers());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS", prior);
            CpuTopology.TestOverride = null;
        }
    }
}
