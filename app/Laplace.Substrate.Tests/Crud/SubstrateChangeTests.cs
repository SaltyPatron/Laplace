using System.Collections.Immutable;
using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.SubstrateCRUD.Tests;

public class SubstrateChangeTests
{
    private static Hash128 H(int seed) => Hash128.Blake3(BitConverter.GetBytes(seed));

    [Fact]
    public void Builder_IntentStagesEmptyByDefault()
    {
        var change = new SubstrateChangeBuilder(H(0), "empty").Build();
        Assert.Equal(0, change.IntentStages.Length);
        Assert.True(change.IntentStages.IsDefaultOrEmpty);
    }

    [Fact]
    public void Builder_ProducesIntentWithAllRows()
    {
        var src = Hash128.OfCanonical("substrate/source/Test/v1");
        var b = new SubstrateChangeBuilder(src, "unit-A");
        b.AddEntity(H(1), 0, H(99));
        b.AddEntity(H(2), 0, H(99));
        b.AddPhysicality(new PhysicalityRow(
            H(3), H(1), src, PhysicalityType.Content,
            0.1, 0.2, 0.3, 0.4,
            Hilbert128.Encode(stackalloc double[] { 0.1, 0.2, 0.3, 0.4 }),
            null, 0, null, null, 0));
        b.AddAttestation(new AttestationRow(
            H(4), H(1), H(50), H(2), src, null,
            AttestationOutcome.Confirm, 0L, 1L, 1_000_000_000L, 30_000_000_000L));

        var change = b.Build();
        Assert.Equal(2, change.Entities.Length);
        Assert.Single(change.Physicalities);
        Assert.Single(change.Attestations);
        Assert.Equal(src, change.Metadata.SourceId);
        Assert.Equal("unit-A", change.Metadata.SourceContentUnitName);
    }

    [Fact]
    public void Builder_IntentIdIsDeterministicAcrossRebuilds()
    {
        SubstrateChange Build()
        {
            var b = new SubstrateChangeBuilder(H(0), "unit");
            b.AddEntity(H(1), 0, H(99));
            b.AddEntity(H(2), 0, H(99));
            return b.Build();
        }
        var a = Build();
        var b2 = Build();
        Assert.Equal(a.Metadata.IntentId, b2.Metadata.IntentId);
    }

    [Fact]
    public void Builder_IntentIdChangesWhenContentChanges()
    {
        var bA = new SubstrateChangeBuilder(H(0), "unit");
        bA.AddEntity(H(1), 0, H(99));
        var bB = new SubstrateChangeBuilder(H(0), "unit");
        bB.AddEntity(H(2), 0, H(99));
        Assert.NotEqual(bA.Build().Metadata.IntentId, bB.Build().Metadata.IntentId);
    }

    [Fact]
    public void Builder_IntentIdChangesWhenSourceChanges()
    {
        var bA = new SubstrateChangeBuilder(H(0), "unit");
        bA.AddEntity(H(1), 0, H(99));
        var bB = new SubstrateChangeBuilder(H(1), "unit");
        bB.AddEntity(H(1), 0, H(99));
        Assert.NotEqual(bA.Build().Metadata.IntentId, bB.Build().Metadata.IntentId);
    }

    [Fact]
    public void Builder_IntentIdChangesWhenUnitNameChanges()
    {
        var bA = new SubstrateChangeBuilder(H(0), "unit-A");
        bA.AddEntity(H(1), 0, H(99));
        var bB = new SubstrateChangeBuilder(H(0), "unit-B");
        bB.AddEntity(H(1), 0, H(99));
        Assert.NotEqual(bA.Build().Metadata.IntentId, bB.Build().Metadata.IntentId);
    }

    [Fact]
    public void Builder_RejectsNullRows()
    {
        var b = new SubstrateChangeBuilder(H(0), "unit");
        Assert.Throws<ArgumentNullException>(() => b.AddEntity((EntityRow)null!));
        Assert.Throws<ArgumentNullException>(() => b.AddPhysicality(null!));
        Assert.Throws<ArgumentNullException>(() => b.AddAttestation(null!));
    }

    [Fact]
    public void Builder_RejectsNullUnitName()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SubstrateChangeBuilder(H(0), null!));
    }

    [Fact]
    public void PhysicalityType_NumericValuesMatchSchemaContract()
    {
        Assert.Equal((short)1, (short)PhysicalityType.Content);
        Assert.Equal((short)2, (short)PhysicalityType.BuildingBlock);
        Assert.Equal((short)3, (short)PhysicalityType.Projection);
    }

    [Fact]
    public void EntityRow_FirstObservedByNullable()
    {
        var r1 = new EntityRow(H(1), 0, H(99), null);
        var r2 = new EntityRow(H(1), 0, H(99), H(2));
        Assert.Null(r1.FirstObservedBy);
        Assert.NotNull(r2.FirstObservedBy);
    }

    [Fact]
    public void AttestationRow_ObjectAndContextNullable()
    {
        var a = new AttestationRow(H(1), H(2), H(3), null, H(4), null,
            AttestationOutcome.Draw, 0, 1, 500_000_000L, 30_000_000_000L);
        Assert.Null(a.ObjectId);
        Assert.Null(a.ContextId);
    }

    [Fact]
    public void PhysicalityRow_TrajectoryNullable()
    {
        var hb = new Hilbert128();
        var p = new PhysicalityRow(H(1), H(2), H(3), PhysicalityType.Content,
            0, 0, 0, 0, hb, null, 0, null, null, 0);
        Assert.Null(p.TrajectoryXyzm);
    }

    [Fact]
    public void Builder_DeduplicatesIdentityRowsWithinIntent()
    {
        var src = Hash128.OfCanonical("substrate/source/Test/v1");
        var b = new SubstrateChangeBuilder(src, "unit-dedup");
        b.AddEntity(H(1), 0, H(99));
        b.AddEntity(H(1), 0, H(99));
        var hb = new Hilbert128();
        var phys = new PhysicalityRow(H(3), H(1), src, PhysicalityType.Content,
            0.1, 0.2, 0.3, 0.4, hb, null, 0, null, null, 0);
        b.AddPhysicality(phys);
        b.AddPhysicality(phys);
        var change = b.Build();
        Assert.Single(change.Entities);
        Assert.Single(change.Physicalities);
    }

    [Fact]
    public void Builder_FoldsRepeatedTestimonyIntoGames()
    {
        var src = Hash128.OfCanonical("substrate/source/Test/v1");
        var b = new SubstrateChangeBuilder(src, "unit-fold");
        for (int i = 0; i < 3; i++)
            b.AddAttestation(new AttestationRow(
                H(4), H(1), H(50), H(2), src, null,
                AttestationOutcome.Confirm, i, 1L, 1_000_000_000L, 30_000_000_000L));
        var a = Assert.Single(b.Build().Attestations);
        Assert.Equal(3L, a.ObservationCount);
        Assert.Equal(3_000_000_000L, a.SumScoreFp1e9);
        Assert.Equal(2L, a.LastObservedAtUnixUs);
        Assert.Equal(AttestationOutcome.Confirm, a.Outcome);
    }

    [Fact]
    public void Builder_FoldNetOutcomeIsClassOfScoreSum()
    {
        var src = Hash128.OfCanonical("substrate/source/Test/v1");
        var b = new SubstrateChangeBuilder(src, "unit-net");
        b.AddAttestation(new AttestationRow(H(4), H(1), H(50), H(2), src, null,
            AttestationOutcome.Confirm, 0L, 1L, 1_000_000_000L, 30_000_000_000L));
        b.AddAttestation(new AttestationRow(H(4), H(1), H(50), H(2), src, null,
            AttestationOutcome.Refute, 0L, 1L, 0L, 30_000_000_000L));
        var a = Assert.Single(b.Build().Attestations);
        Assert.Equal(AttestationOutcome.Draw, a.Outcome);
        Assert.Equal(2L, a.ObservationCount);
    }

    [Fact]
    public void Builder_FoldRejectsMixedPhi()
    {
        var src = Hash128.OfCanonical("substrate/source/Test/v1");
        var b = new SubstrateChangeBuilder(src, "unit-phi");
        b.AddAttestation(new AttestationRow(H(4), H(1), H(50), H(2), src, null,
            AttestationOutcome.Confirm, 0L, 1L, 1_000_000_000L, 30_000_000_000L));
        Assert.Throws<InvalidOperationException>(() =>
            b.AddAttestation(new AttestationRow(H(4), H(1), H(50), H(2), src, null,
                AttestationOutcome.Confirm, 0L, 1L, 1_000_000_000L, 99_000_000_000L)));
    }
}
