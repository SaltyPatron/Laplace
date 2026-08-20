using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

[Collection("CpuTopology")]
public sealed class IngestSizingTests
{
    private const long TestBudgetBytes = 2L << 30;

    [Fact]
    public void Resolve_14900KLikeTopology_MatchesApplyPartitions()
    {
        var plan = IngestSizing.Resolve(
            8, 6, 8, workingSetBudgetBytes: TestBudgetBytes, composeWorkers: 7);
        Assert.Equal(IngestSizing.ResolveRecordBatch(
            8, composeWorkers: 7, workingSetBudgetBytes: TestBudgetBytes),
            plan.RecordBatchSize);
        Assert.Equal(IngestSizing.ResolveApplyIo(8).ProbeChunkIds, plan.ProbeChunkSize);
        Assert.Equal(IngestSizing.ResolveFlushEnvelopeRecordCap(
            IngestSourceProfile.Default), plan.CommitRows);
        Assert.Equal((plan.CommitRows + plan.RecordBatchSize - 1) / plan.RecordBatchSize,
            plan.MaxIntentsPerCommit);
        Assert.Equal(7 + 6 + 8, plan.DecomposeChannelCapacity);
        Assert.Equal(6 + 8, plan.FileWorkerChannelDepth);
        Assert.Equal((long)plan.CommitRows * plan.DecomposeChannelCapacity, plan.RowBudget);
    }

    [Fact]
    public void ResolveMaxIntentsPerCommit_LargeRowBudget_AllowsMoreThanOmwCap()
    {
        int n = IngestSizing.ResolveMaxIntentsPerCommit(2048, 250_000, 250_000);
        Assert.Equal((250_000 + 2047) / 2048, n);
    }

    [Fact]
    public void Resolve_MoreComposeWorkersDivideOneBatchEnvelope()
    {
        var two = IngestSizing.Resolve(
            4, 2, 4, workingSetBudgetBytes: TestBudgetBytes, composeWorkers: 2);
        var four = IngestSizing.Resolve(
            4, 2, 4, workingSetBudgetBytes: TestBudgetBytes, composeWorkers: 4);
        Assert.Equal(two.RecordBatchSize / 2, four.RecordBatchSize);
        Assert.Equal(two.CommitRows, four.CommitRows);
    }

    [Fact]
    public void Resolve_EnvBatchOverride_RecomputesCommit()
    {
        var plan = IngestSizing.Resolve(
            8, 6, 8, recordBatchOverride: 4096, workingSetBudgetBytes: TestBudgetBytes);
        Assert.Equal(4096, plan.RecordBatchSize);
        Assert.Equal(IngestSizing.ResolveFlushEnvelopeRecordCap(
            IngestSourceProfile.Default), plan.CommitRows);
    }

    [Fact]
    public void Resolve_RelationTripleProfile_SmallBatchAndCommitOnBudget()
    {
        var plan = IngestSizing.Resolve(
            8, 6, 1, profile: IngestSourceProfile.RelationTriple, workingSetBudgetBytes: TestBudgetBytes);
        Assert.Equal(IngestSizing.ResolveRecordBatch(
            8,
            IngestSourceProfile.RelationTriple.EstBytesPerRecord,
            IngestSourceProfile.RelationTriple.EstComposeUnitsPerRecord,
            workingSetBudgetBytes: TestBudgetBytes,
            residentBytesPerComposeUnit: IngestSourceProfile.RelationTriple.ResidentBytesPerComposeUnit),
            plan.RecordBatchSize);
        Assert.Equal(IngestSizing.ResolveFlushEnvelopeRecordCap(
            IngestSourceProfile.RelationTriple), plan.CommitRows);
        Assert.Equal(plan.CommitRows, IngestSizing.ResolveWorkingSetProbeInterval(
            plan.RecordBatchSize, IngestSourceProfile.RelationTriple));
    }

    [Fact]
    public void ResolveWorkingSetBudgetBytes_DividesRamAcrossActualResidentOwners()
    {
        long budget = IngestSizing.ResolveWorkingSetBudgetBytes();
        int partitions = CpuTopology.ResolveApplyPartitions();
        long expected = Math.Max(
            (long)MemoryTopology.CopyStartupBytesPerConnection * partitions,
            PostgresResourcePlan.Current.ClientBudgetBytes / (partitions + 4));
        Assert.Equal(expected, budget);
        Assert.Equal(partitions + 4, MemoryTopology.WorkingSetResidentOwners);
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
    public void ResolveWorkingSetProbeInterval_RawIntervalAboveFlushCap_ClampsToCap()
    {
        // This profile drives the envelope-derived cap below one record while
        // raw=4*1000. One record is the forward-progress floor; the probe interval
        // must never outrun it.
        var profile = new IngestSourceProfile(
            EstBytesPerRecord: 1_000_000,
            EstComposeUnitsPerRecord: 1_000);
        int flushCap = IngestSizing.ResolveFlushEnvelopeRecordCap(profile);

        Assert.Equal(1, flushCap);
        Assert.True(4 * profile.EstComposeUnitsPerRecord > flushCap);
        Assert.Equal(flushCap,
            IngestSizing.ResolveWorkingSetProbeInterval(4, profile));
    }

    [Fact]
    public void ResolveWorkingSetFlushEnvelope_DividesOneProcessBudgetAcrossConcurrentSets()
    {
        long one = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(1);
        long ten = IngestSizing.ResolveWorkingSetFlushEnvelopeBytes(10);

        Assert.Equal(one / 10, ten);
        Assert.True(ten * 10 <= one);
    }

    [Fact]
    public void UdResidentTreeEstimate_AndTenWorkers_ShareOneEnvelope()
    {
        var profile = IngestSourceProfile.UdSentence;
        long processEnvelope = 512L << 20;
        long workerEnvelope = processEnvelope / 10;

        int oneWorkerCap = IngestSizing.ResolveFlushEnvelopeRecordCap(
            profile, processEnvelope);
        int tenWorkerCap = IngestSizing.ResolveFlushEnvelopeRecordCap(
            profile, workerEnvelope);

        Assert.Equal(418, oneWorkerCap);
        Assert.Equal(41, tenWorkerCap);
        Assert.True(
            tenWorkerCap * 10L * profile.UncomposedResidentBytesPerRecord
            <= processEnvelope);
    }

    [Fact]
    public void ResolveForSource_RelationTriple_UsesTopologyAndMemory()
    {
        IngestTopology.EnsureReady();
        var plan = IngestSizing.ResolveForSource(IngestSourceProfile.RelationTriple);
        Assert.True(plan.RecordBatchSize > 0);
        Assert.Equal(plan.CommitRows, plan.WorkingSetRecordCap);
        Assert.Equal(plan.WorkingSetProbeInterval,
            IngestSizing.ResolveWorkingSetProbeInterval(plan.RecordBatchSize, IngestSourceProfile.RelationTriple));
        Assert.Equal(IngestTopology.Current.ComposeWorkers, plan.ComposeWorkers);
        Assert.Equal(IngestTopology.Current.IoWorkersAvailable, plan.IoWorkersAvailable);
        Assert.Equal(IngestTopology.Current.ApplyPartitions, plan.ApplyPartitions);
        Assert.Equal(MemoryTopology.WorkingSetBudgetBytes, plan.WorkingSetBudgetBytes);
    }

    [Fact]
    public void Resolve_UnicodeProfile_LargeBatch()
    {
        var plan = IngestSizing.Resolve(
            8, 6, 1, profile: IngestSourceProfile.Unicode, workingSetBudgetBytes: TestBudgetBytes);
        Assert.Equal(IngestSizing.ResolveRecordBatch(
            8,
            IngestSourceProfile.Unicode.EstBytesPerRecord,
            IngestSourceProfile.Unicode.EstComposeUnitsPerRecord,
            workingSetBudgetBytes: TestBudgetBytes,
            residentBytesPerComposeUnit: IngestSourceProfile.Unicode.ResidentBytesPerComposeUnit),
            plan.RecordBatchSize);
        Assert.Equal(IngestSizing.ResolveFlushEnvelopeRecordCap(
            IngestSourceProfile.Unicode), plan.CommitRows);
    }

    [Fact]
    public void EstimateApplyGateBytes_ZeroSurcharge_MatchesTupleBill()
    {
        // Surcharge must stay 0: MEASURED chess regress when non-zero (shared
        // present att ids re-merged per small apply). Gate bytes = tuple bill.
        Assert.Equal(0, IngestSizing.AttestationApplySurchargeBytes);
        long gated = IngestSizing.EstimateApplyGateBytes(
            10, 20, 100, trajectoryBytes: 0, intentStageTupleBytes: 500, intentStageAttestationCount: 50);
        Assert.Equal(
            (10L + 20 + 100) * IngestSizing.ApplyTupleByteEstimate + 500,
            gated);
    }

    [Fact]
    public void Resolve_ChessPgnProfile_AllowsParallelIntentsOn12CoreBudget()
    {
        // Hart-server-shaped: 12 apply partitions, 11 compose workers, 4 GiB WS.
        // The retired 4_000_000 EstBytesPerRecord collapsed this to max_intents=1.
        var plan = IngestSizing.Resolve(
            performanceCoreCount: 12,
            fileWorkers: 10,
            applyPartitions: 12,
            profile: IngestSourceProfile.ChessPgn,
            workingSetBudgetBytes: 4L << 30,
            composeWorkers: 11);
        Assert.True(plan.RecordBatchSize > 0);
        Assert.True(plan.CommitRows >= plan.RecordBatchSize * 2);
        Assert.True(plan.MaxIntentsPerCommit >= 3);
    }

    [Fact]
    public void ResolveMaxIntentsPerCommit_SmallCommitAboveTwoBatches_NotSerializedToOne()
    {
        // commit_rows=429, batch=256: old formula → 429/(256*8)=0 → max_intents=1.
        // Pin the post-fix floor: a commit that holds ≥2 batches must not serialize to 1.
        int n = IngestSizing.ResolveMaxIntentsPerCommit(256, 429);
        Assert.True(n >= 2);
    }

    [Fact]
    public void ResolveConsensusFold_DerivesEveryCountFromOneEnvelopeAndTopology()
    {
        var plan = IngestSizing.ResolveConsensusFold(
            applyPartitions: 12,
            workingSetBudgetBytes: 4L << 30,
            flushEnvelopeBytes: 512L << 20);

        Assert.Equal(12, plan.Connections);
        Assert.Equal((512L << 20) / 12 / MemoryTopology.ConsensusFoldTransitBytesPerCell,
            plan.ChunkCells);
        Assert.Equal(8, plan.PipelineDepth);
        Assert.Equal((512L << 20) / MemoryTopology.ConsensusFoldBytesPerRelation,
            plan.DeltaCapacityCells);
        Assert.Equal((512L << 20) / MemoryTopology.ConsensusMaskPairResidentBytes,
            plan.MaskPairCapacity);

        // A resource equation may legitimately evaluate to any integer, including
        // a power of two. Pin the equation, not a blacklist of the retired literal.
        var halfEnvelope = IngestSizing.ResolveConsensusFold(
            applyPartitions: 12,
            workingSetBudgetBytes: 4L << 30,
            flushEnvelopeBytes: 256L << 20);
        Assert.Equal(plan.ChunkCells / 2, halfEnvelope.ChunkCells);
    }

    [Fact]
    public void AllocateFoldRunWidths_DistributesConnectionsByActualRunLoad()
    {
        Assert.Equal(new[] { 12 }, IngestSizing.AllocateFoldRunWidths([120], 12));
        Assert.Equal(new[] { 10, 1, 1 }, IngestSizing.AllocateFoldRunWidths([100, 10, 10], 12));
        Assert.Equal(new[] { 1, 1, 1 }, IngestSizing.AllocateFoldRunWidths([1, 1, 1], 12));
        Assert.All(IngestSizing.AllocateFoldRunWidths(Enumerable.Repeat(10, 20).ToArray(), 12),
            width => Assert.Equal(1, width));
    }

    [Fact]
    public void ResolveConsensusFold_MoreConnectionsDivideRatherThanMultiplyMemory()
    {
        var four = IngestSizing.ResolveConsensusFold(4, 2L << 30, 256L << 20);
        var eight = IngestSizing.ResolveConsensusFold(8, 2L << 30, 256L << 20);

        Assert.Equal(four.ChunkCells / 2, eight.ChunkCells);
        Assert.True((long)eight.ChunkCells * eight.Connections
            * MemoryTopology.ConsensusFoldTransitBytesPerCell <= 256L << 20);
        Assert.Equal(four.PipelineDepth, eight.PipelineDepth);
    }

    [Fact]
    public void ResolveConsensusFold_ConstrainedBudgetKeepsOneBoundedPipelineSlot()
    {
        var plan = IngestSizing.ResolveConsensusFold(4, 256L << 20, 256L << 20);

        Assert.Equal(1, plan.PipelineDepth);
        Assert.True((long)plan.ChunkCells * plan.Connections
            * MemoryTopology.ConsensusFoldTransitBytesPerCell <= 256L << 20);
    }


    [Fact]
    public void ResolveApplyIo_DerivesCountsFromSharedBytesAndConnections()
    {
        var plan = IngestSizing.ResolveApplyIo(
            applyPartitions: 12,
            workingSetBudgetBytes: 4L << 30,
            flushEnvelopeBytes: 512L << 20);

        Assert.Equal(12, plan.Connections);
        Assert.Equal((int)((512L << 20) / 12
            / MemoryTopology.PresenceProbeTransitBytesPerId), plan.ProbeChunkIds);
        Assert.Equal((int)((512L << 20) / 12
            / MemoryTopology.AttestationMergeTransitBytesPerRow), plan.MergeChunkRows);
        Assert.Equal((512L << 20) / 9, plan.CacheBytesPerOwner);
        Assert.Equal((int)((512L << 20) / 9
            / MemoryTopology.ConcurrentHash128ResidentBytes), plan.EntityPresenceCacheIds);
        Assert.Equal(plan.EntityPresenceCacheIds, plan.PhysicalityPresenceCacheIds);
        Assert.Equal(plan.EntityPresenceCacheIds, plan.LadderCacheIds);
        Assert.Equal(plan.EntityPresenceCacheIds, plan.ReaderProvenCacheIds);
        Assert.Equal((int)((512L << 20) / 9
            / MemoryTopology.ConcurrentHash128PairResidentBytes), plan.ReaderRootCacheIds);
        Assert.Equal(plan.ReaderRootCacheIds, plan.TextRootCacheIds);
        Assert.Equal(plan.ReaderRootCacheIds, plan.ImageRootCacheIds);
        Assert.Equal(plan.ReaderRootCacheIds, plan.AudioRootCacheIds);
    }

    [Theory]
    [InlineData(0, 0, 12, 1)]
    [InlineData(1, 1, 12, 1)]
    [InlineData(12, 8 * 1024, 12, 1)]
    [InlineData(12, 12 * 8 * 1024, 12, 12)]
    [InlineData(4, 12 * 8 * 1024, 12, 4)]
    public void ResolveCopyConnections_UsesPayloadBytesRowsAndTopology(
        int rows, long bytes, int workers, int expected)
    {
        Assert.Equal(expected, IngestSizing.ResolveCopyConnections(
            rows, bytes, workers, MemoryTopology.CopyStartupBytesPerConnection));
    }
}
