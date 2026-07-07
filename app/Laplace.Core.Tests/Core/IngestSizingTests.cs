using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class IngestSizingTests
{
    private const long TestBudgetBytes = 2L << 30;

    [Fact]
    public void Resolve_14900KLikeTopology_MatchesApplyPartitions()
    {
        var plan = IngestSizing.Resolve(8, 6, 8, workingSetBudgetBytes: TestBudgetBytes);
        Assert.Equal(2048, plan.RecordBatchSize);
        Assert.Equal(512, plan.ProbeChunkSize);
        Assert.Equal(32_768, plan.CommitRows);
        Assert.Equal(2, plan.MaxIntentsPerCommit);
        Assert.Equal(38, plan.DecomposeChannelCapacity);
        Assert.Equal(18, plan.FileWorkerChannelDepth);
        Assert.Equal((long)plan.CommitRows * plan.DecomposeChannelCapacity, plan.RowBudget);
    }

    [Fact]
    public void ResolveMaxIntentsPerCommit_LargeRowBudget_AllowsMoreThanOmwCap()
    {
        int n = IngestSizing.ResolveMaxIntentsPerCommit(2048, 250_000, 250_000);
        Assert.InRange(n, 9, 48);
    }

    [Fact]
    public void Resolve_DefaultApplyPartitions_OneBulkApplyPerCommit()
    {
        Assert.Equal(1, IngestTopology.ResolveApplyPartitions());
    }

    [Fact]
    public void Resolve_SmallCoreCount_ShrinksBatch()
    {
        var plan = IngestSizing.Resolve(4, 2, 4, workingSetBudgetBytes: TestBudgetBytes);
        Assert.InRange(plan.RecordBatchSize, 512, 2048);
        Assert.True(plan.CommitRows >= plan.RecordBatchSize);
    }

    [Fact]
    public void Resolve_EnvBatchOverride_RecomputesCommit()
    {
        var plan = IngestSizing.Resolve(
            8, 6, 8, recordBatchOverride: 4096, workingSetBudgetBytes: TestBudgetBytes);
        Assert.Equal(4096, plan.RecordBatchSize);
        Assert.Equal(65_536, plan.CommitRows);
    }

    [Fact]
    public void Resolve_RelationTripleProfile_SmallBatchAndCommitOnBudget()
    {
        var plan = IngestSizing.Resolve(
            8, 6, 1, profile: IngestSourceProfile.RelationTriple, workingSetBudgetBytes: TestBudgetBytes);
        Assert.Equal(1024, plan.RecordBatchSize);
        Assert.Equal(2048, plan.CommitRows);
        Assert.Equal(2048, IngestSizing.ResolveWorkingSetProbeInterval(plan.RecordBatchSize,
            IngestSourceProfile.RelationTriple));
    }

    [Fact]
    public void Resolve_UnicodeProfile_LargeBatch()
    {
        var plan = IngestSizing.Resolve(
            8, 6, 1, profile: IngestSourceProfile.Unicode, workingSetBudgetBytes: TestBudgetBytes);
        Assert.Equal(4096, plan.RecordBatchSize);
        Assert.Equal(8192, plan.CommitRows);
    }
}
