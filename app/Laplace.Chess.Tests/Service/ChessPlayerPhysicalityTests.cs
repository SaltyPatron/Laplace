using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessPlayerPhysicalityTests
{
    [Fact]
    public void PlayerIdentity_OwnsItsWitnessedNameTrajectory()
    {
        const string name = "MagnusCarlsen";
        var playerId = ChessVocabulary.PlayerId(name);
        var builder = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/player");

        ChessVocabulary.EmitPlayer(builder, playerId, name, ChessVocabulary.PgnSourceId);
        var change = builder.Build();

        Assert.Contains(change.Entities,
            e => e.Id == playerId && e.TypeId == ChessVocabulary.PlayerType);
        var placement = Assert.Single(change.Physicalities,
            p => p.EntityId == playerId && p.Type == PhysicalityType.Content);
        Assert.Equal(1, placement.NConstituents);
        Assert.Equal(
            [ContentEmitter.RootId(name)!.Value],
            Trajectory.Constituents(placement.TrajectoryXyzm!));
        Assert.All(
            new[] { placement.CoordX, placement.CoordY, placement.CoordZ, placement.CoordM },
            static value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void PlayerAliases_DoNotReplaceTheCanonicalPlacementWithinAChange()
    {
        var playerId = ChessVocabulary.PlayerId("MagnusCarlsen");
        var builder = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/player-alias");

        ChessVocabulary.EmitPlayer(builder, playerId, "MagnusCarlsen", ChessVocabulary.PgnSourceId);
        ChessVocabulary.EmitPlayer(builder, playerId, "Carlsen, Magnus", ChessVocabulary.PgnSourceId);
        var change = builder.Build();

        var placement = Assert.Single(change.Physicalities,
            p => p.EntityId == playerId && p.Type == PhysicalityType.Content);
        Assert.Equal(
            [ContentEmitter.RootId("MagnusCarlsen")!.Value],
            Trajectory.Constituents(placement.TrajectoryXyzm!));
        Assert.Equal(2, change.Attestations.Count(a =>
            a.SubjectId == playerId
            && a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS")));
    }

    [Fact]
    public void ExistingPlayerRepair_AddsOnlyPhysicalityAndUsesItsOwnTrunkIdentity()
    {
        var playerId = ChessVocabulary.PlayerId("MagnusCarlsen");
        var builder = new SubstrateChangeBuilder(
            ChessVocabulary.TrajectorySourceId, "test/player-repair");

        ChessVocabulary.AppendPlayerPhysicality(
            builder, playerId, "MagnusCarlsen", ChessVocabulary.TrajectorySourceId);
        var change = builder.Build();
        var record = ChessTrajectoryRecord.ForPlayer(playerId, "MagnusCarlsen");

        Assert.Empty(change.Attestations);
        Assert.Single(change.Physicalities, p => p.EntityId == playerId);
        Assert.Equal(
            PhysicalityId.Compute(playerId, PhysicalityType.Content),
            record.TrunkRootId);
    }
}
