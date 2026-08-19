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
    public void EntityAdmission_SeparatesComposedContentFromGovernedIdentity()
    {
        var source = Hash128.Blake3("admission-test-source"u8);
        var word = Hash128.Blake3("missing-placement"u8);
        var pos = Hash128.Blake3("probationary-pos"u8);
        var tracker = new EntityAdmissionTracker();

        tracker.Observe(new SubstrateChangeBuilder(source, "word")
            .AddEntity(word, EntityTier.Word, EntityTypeRegistry.Word, source)
            .Build());
        tracker.Observe(new SubstrateChangeBuilder(source, "pos")
            .AddEntity(pos, EntityTier.Word, EntityTypeRegistry.Pos, source)
            .Build());

        var pending = Assert.Single(tracker.SnapshotPendingContent());
        Assert.Equal(word, pending.Id);
        Assert.Equal(1, tracker.GovernedWithoutPhysicalityCount);
        Assert.True(EntityIdentityPolicy.RequiresPhysicality(EntityTypeRegistry.Word));
        Assert.False(EntityIdentityPolicy.RequiresPhysicality(EntityTypeRegistry.Pos));
        Assert.False(EntityIdentityPolicy.RequiresPhysicality(EntityTypeRegistry.Ordinal));
    }
}
