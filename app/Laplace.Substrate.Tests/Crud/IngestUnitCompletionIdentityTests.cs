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
    public void CompletingAUnitAddsNoTestimonyAndPreservesTheSourceObservationIdentity(int layer)
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
        var peer = SubstrateCanonicalIds.Source("UnitIdentityPeer");
        var peerCompleted = Build(true, peer);
        Assert.Equal(before.Metadata.IntentId, completed.Metadata.IntentId);
        Assert.Equal(before.Metadata.IntentId, peerCompleted.Metadata.IntentId);
        Assert.Equal(new IngestUnitCompletionKey(source, marker, layer), Assert.Single(completed.UnitCompletions));
        Assert.Equal(new IngestUnitCompletionKey(peer, marker, layer), Assert.Single(peerCompleted.UnitCompletions));
        Assert.Empty(before.UnitCompletions);
        // Completion is never testimony and never an entity.
        Assert.Equal(before.Attestations.Select(row => row.Id), completed.Attestations.Select(row => row.Id));
        Assert.Equal(before.Entities.Select(row => row.Id), completed.Entities.Select(row => row.Id));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void CompletionLayerCannotEscapeTheByteWideLayerRange(int layer)
        => Assert.Throws<ArgumentOutOfRangeException>(() => IngestUnitCompletion.Key(
            Hash128.OfCanonical("test/unit"), Hash128.OfCanonical("test/owner"), layer));
}
