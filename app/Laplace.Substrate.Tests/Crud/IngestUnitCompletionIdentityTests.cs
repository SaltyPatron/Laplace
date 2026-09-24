using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class IngestUnitCompletionIdentityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(255)]
    public void AddingAnOperationalReceiptPreservesTheExistingSourceObservationIdentity(int layer)
    {
        CodepointPerfcache.LoadDefault();
        var source = SubstrateCanonicalIds.Source("UnitIdentityTest");
        var marker = Hash128.OfCanonical("test/completed-unit");
        var relation = RelationTypeRegistry.RelationTypeId("IS_A");
        var obj = Hash128.OfCanonical("test/completed-unit-object");
        SubstrateChange Build(bool complete, Hash128 owner)
        {
            using var builder = new SubstrateChangeBuilder(source, "same-actual-source-unit")
                .AddEntity(marker, EntityTier.Document, EntityTypeRegistry.SourceReference)
                .AddAttestation(NativeAttestation.CategoricalResolved(
                    marker, relation, obj, source, null, 0.9));
            if (complete) IngestUnitCompletion.Emit(builder, marker, owner, layer);
            return builder.Build();
        }

        var before = Build(false, source);
        var completed = Build(true, source);
        var peerCompleted = Build(true, SubstrateCanonicalIds.Source("UnitIdentityPeer"));
        Assert.Equal(before.Metadata.IntentId, completed.Metadata.IntentId);
        Assert.Equal(before.Metadata.IntentId, peerCompleted.Metadata.IntentId);
        Assert.Single(completed.Attestations,
            row => row.Id == IngestUnitCompletion.AttestationId(marker, source, layer));
        Assert.NotEqual(completed.Attestations[^1].Id, peerCompleted.Attestations[^1].Id);
        Assert.Equal(before.Attestations[0].Id, completed.Attestations[0].Id);
        Assert.True(completed.Entities.Length > before.Entities.Length);
        Assert.True(completed.Attestations.Length > before.Attestations.Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void ReceiptNamespaceCannotEscapeTheOperationalExclusionEnvelope(int layer)
        => Assert.Throws<ArgumentOutOfRangeException>(() => IngestUnitCompletion.RelationTypeId(layer));
}
