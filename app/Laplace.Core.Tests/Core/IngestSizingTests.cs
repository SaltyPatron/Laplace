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
        // batch*16 clamped to [2048, 32768] — the big-source probe-chunk law
        // (fee9e1f): presence probes are round-trip dominated, match the
        // WS-apply probe scale instead of thousands of serial 512-id trips.
        Assert.Equal(32_768, plan.ProbeChunkSize);
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
    public void ResolveWorkingSetBudgetBytes_On48GiBMachine_IsAboutThreeGiB()
    {
        long budget = IngestSizing.ResolveWorkingSetBudgetBytes();
        Assert.InRange(budget, 1L << 30, 8L << 30);
    }

    [Fact]
    public void ResolveWorkingSetRecordCap_RelationTriple_MatchesCommitRows()
    {
        IngestTopology.EnsureReady();
        int cap = IngestSizing.ResolveWorkingSetRecordCap(
            IngestSourceProfile.RelationTriple, TestBudgetBytes);
        var plan = IngestSizing.ResolveForSource(
            IngestSourceProfile.RelationTriple, workingSetBudgetBytes: TestBudgetBytes);
        Assert.Equal(plan.CommitRows, cap);
    }

    [Fact]
    public void ResolveForSource_RelationTriple_UsesTopologyAndMemory()
    {
        IngestTopology.EnsureReady();
        var plan = IngestSizing.ResolveForSource(IngestSourceProfile.RelationTriple);
        Assert.InRange(plan.RecordBatchSize, 256, 4096);
        Assert.Equal(plan.CommitRows, plan.WorkingSetRecordCap);
        Assert.Equal(plan.WorkingSetProbeInterval,
            IngestSizing.ResolveWorkingSetProbeInterval(plan.RecordBatchSize, IngestSourceProfile.RelationTriple));
        Assert.Equal(IngestTopology.Current.ComposeWorkers, plan.ComposeWorkers);
        Assert.InRange(plan.WorkingSetBudgetBytes, 1L << 30, 8L << 30);
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
