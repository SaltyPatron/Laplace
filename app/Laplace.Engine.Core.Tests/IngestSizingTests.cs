using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class IngestSizingTests
{
    [Fact]
    public void Resolve_14900KLikeTopology_MatchesApplyPartitions()
    {
        // 8 P-cores, 6 file workers, 8 apply partitions
        var plan = IngestSizing.Resolve(8, 6, 8);
        Assert.Equal(2048, plan.RecordBatchSize);
        Assert.Equal(42, plan.ProbeChunkSize); // 2048 / (6*8)
        Assert.Equal(50_000, plan.CommitRows);
        Assert.Equal(8, plan.MaxIntentsPerCommit);
        Assert.Equal(38, plan.DecomposeChannelCapacity);
        Assert.Equal(18, plan.FileWorkerChannelDepth); // 6 workers × 3 slots (ceil(16/6))
        Assert.Equal((long)plan.CommitRows * plan.DecomposeChannelCapacity, plan.RowBudget);
    }

    [Fact]
    public void Resolve_SmallCoreCount_ShrinksBatch()
    {
        var plan = IngestSizing.Resolve(4, 2, 4);
        Assert.InRange(plan.RecordBatchSize, 512, 2048);
        Assert.True(plan.CommitRows >= 50_000);
    }

    [Fact]
    public void Resolve_EnvBatchOverride_RecomputesCommit()
    {
        var plan = IngestSizing.Resolve(8, 6, 8, recordBatchOverride: 4096);
        Assert.Equal(4096, plan.RecordBatchSize);
        Assert.Equal(4096 * 8 * 2, plan.CommitRows);
    }
}
