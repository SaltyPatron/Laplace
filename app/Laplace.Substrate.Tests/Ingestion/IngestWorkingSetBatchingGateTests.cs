using Xunit;
using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD;

namespace Laplace.Ingestion.Tests;

/// <summary>
/// Guards the separation between compose memory fragments and database apply batches.
/// A multi-file worker may close tiny compose sets to release deferred trees; the runner
/// must not turn their count into a transaction/probe/fold cadence.
/// </summary>
public sealed class IngestWorkingSetBatchingGateTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(499_999, false)]
    [InlineData(500_000, true)]
    public void WorkingSetFlushPolicy_UsesFinalizedPayloadBytes(
        long bytes, bool expected)
    {
        Assert.Equal(expected, IngestRunner.ShouldFlushWorkingSet(
            bytes, byteCap: 500_000));
    }

    [Fact]
    public void WorkingSetSourceBoundary_FlushesOnlyWhenVendorChanges()
    {
        var semLink = Hash128.Blake3("sem-link-source"u8);
        var predicateMatrix = Hash128.Blake3("predicate-matrix-source"u8);

        Assert.False(IngestRunner.ShouldFlushWorkingSetSourceBoundary(null, semLink));
        Assert.False(IngestRunner.ShouldFlushWorkingSetSourceBoundary(semLink, semLink));
        Assert.True(IngestRunner.ShouldFlushWorkingSetSourceBoundary(
            predicateMatrix, semLink));
    }

    [Fact]
    public void EntityAdmission_TracksEveryUnplacedEntity()
    {
        var source = Hash128.Blake3("admission-test-source"u8);
        var word = Hash128.Blake3("missing-placement"u8);
        var pos = Hash128.Blake3("probationary-pos"u8);
        var tracker = new EntityAdmissionTracker();

        tracker.Observe(new SubstrateChangeBuilder(source, "word")
            .AddEntity(word, EntityTier.Word, EntityTypeRegistry.Word)
            .Build());
        tracker.Observe(new SubstrateChangeBuilder(source, "pos")
            .AddEntity(pos, EntityTier.Word, EntityTypeRegistry.Pos)
            .Build());

        var pending = tracker.SnapshotPendingContent().Select(static item => item.Id).ToHashSet();
        Assert.Equal(2, pending.Count);
        Assert.Contains(word, pending);
        Assert.Contains(pos, pending);
        Assert.Equal(0, tracker.GovernedWithoutPhysicalityCount);
    }

    [Fact]
    public void FileBackedApply_CoalescesTinyFilesBySourceUntilCapacity()
    {
        var root = Laplace.Decomposers.Abstractions.Tests.TypeIdLawTests.FindRepoRootPublic();
        var source = File.ReadAllText(Path.Combine(
            root, "app", "Laplace.Substrate", "Ingestion", "IngestRunner.cs"));

        Assert.Contains("Dictionary<Hash128, ApplyBatchBucket>", source);
        Assert.Contains("Hash128 owner = intent.Metadata.SourceId", source);
        Assert.DoesNotContain("if (terminal && bucket.Batch.Count > 0)", source);
        Assert.DoesNotContain("|| IsPeriodBoundaryIntent(intent)", source);
        Assert.Contains("_writer.CompleteFileAsync(fileLabel", source);
    }

}
