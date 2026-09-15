using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// A reusable LINE owns the exact content trajectory named by its Merkle identity:
/// start position followed by ordered typed moves. Its full board walk is a separate
/// deterministic projection.
/// </summary>
public sealed class ChessGameTrajectoryTests
{
    private const string Game =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    private static SubstrateChange Compose()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/pgn");
        ChessPgnDecomposer.ComposeGame(parsed, b, analyzeInline: true);
        return b.SetInputUnitsConsumed(1).Build();
    }

    private static ChessGameRecord Parsed() => ChessPgnDecomposer.TryParseGame(Game)!;

    private static PhysicalityRow LineTrajectory(SubstrateChange change, PhysicalityType type)
    {
        var gameEntity = Assert.Single(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        return Assert.Single(change.Physicalities,
            p => p.EntityId == gameEntity.Id && p.Type == type);
    }

    [Fact]
    public void Game_CarriesATrajectory()
    {
        var traj = LineTrajectory(Compose(), PhysicalityType.Content);
        Assert.NotNull(traj.TrajectoryXyzm);
        Assert.NotEmpty(traj.TrajectoryXyzm!);
    }

    [Fact]
    public void ContentTrajectory_HasStartPositionPlusOneVertexPerMove()
    {
        var traj = LineTrajectory(Compose(), PhysicalityType.Content);
        Assert.Equal(8, traj.NConstituents);
        Assert.Equal(8 * 4, traj.TrajectoryXyzm!.Length);
    }

    [Fact]
    public void ContentTrajectory_InvertsBackToTheExactMerklePreimage()
    {
        var parsed = Parsed();
        var traj = LineTrajectory(Compose(), PhysicalityType.Content);
        var recovered = Trajectory.Constituents(traj.TrajectoryXyzm!);
        var expected = new[] { parsed.PositionIds[0] }.Concat(parsed.MoveIds).ToArray();
        Assert.Equal(expected, recovered);
        Assert.Equal(parsed.LineId, Hash128.Merkle(EntityTier.Document, recovered));
    }

    [Fact]
    public void Trajectory_ReferencesDeduplicatedPositionContentWithLosslessPhysicality()
    {
        var change = Compose();
        var recovered = Trajectory.Constituents(
            LineTrajectory(change, PhysicalityType.Projection).TrajectoryXyzm!);
        var deposited = change.Entities
            .Where(e => e.TypeId == ChessVocabulary.PositionType)
            .Select(e => e.Id)
            .ToHashSet();

        Assert.Equal(Parsed().PositionIds, recovered);
        Assert.Equal(recovered.ToHashSet(), deposited);
        Assert.Equal(deposited.Count, change.Physicalities.Count(p =>
            p.Type == PhysicalityType.Content && deposited.Contains(p.EntityId)));

        foreach (var position in change.Entities.Where(e => deposited.Contains(e.Id)))
        {
            var physicality = Assert.Single(change.Physicalities, p =>
                p.EntityId == position.Id && p.Type == PhysicalityType.Content);
            var constituents = Trajectory.Constituents(physicality.TrajectoryXyzm!);
            Assert.Equal(physicality.NConstituents, constituents.Length);
            Assert.Equal(position.Id, Hash128.Merkle(position.Tier, constituents));
        }
    }

    [Fact]
    public void Trajectory_IsAPhysicalityNotPartOfTheGameId()
    {
        var change = Compose();
        var gameEntity = Assert.Single(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        var parsed = Parsed();
        Assert.Equal(gameEntity.Id,
            ChessCompose.LineId(parsed.PositionIds[0], parsed.MoveIds));
    }

    [Fact]
    public void Trajectory_IsDeterministic()
    {
        var a = LineTrajectory(Compose(), PhysicalityType.Content);
        var b = LineTrajectory(Compose(), PhysicalityType.Content);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.TrajectoryXyzm, b.TrajectoryXyzm);
        Assert.Equal(a.CoordX, b.CoordX);
        Assert.Equal(a.CoordY, b.CoordY);
        Assert.Equal(a.CoordZ, b.CoordZ);
        Assert.Equal(a.CoordM, b.CoordM);
    }

    [Fact]
    public void NoAnalyze_KeepsContentTrajectoryButOmitsPositionProjection()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/pgn");
        ChessPgnDecomposer.ComposeGame(parsed, b, analyzeInline: false);
        var change = b.SetInputUnitsConsumed(1).Build();

        var gameEntity = Assert.Single(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        Assert.Contains(change.Physicalities,
            p => p.EntityId == gameEntity.Id && p.Type == PhysicalityType.Content);
        Assert.DoesNotContain(change.Physicalities,
            p => p.EntityId == gameEntity.Id && p.Type == PhysicalityType.Projection);
    }

    private static SubstrateChange ComposeBackfill()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var witnessed = new ChessWitnessedGame(
            LineId: parsed.LineId,
            PlayingId: parsed.PlayingId,
            Moves: ["e4", "e5", "Qh5", "Nc6", "Bc4", "Nf6", "Qxf7#"],
            Result: GameOutcome.WonBy(0),
            WhitePlayer: null, BlackPlayer: null, StartFen: null,
            ClockTokens: null, EvalTokens: null, QualityTokens: null);

        var b = new SubstrateChangeBuilder(ChessVocabulary.TrajectorySourceId, "test/trajectory");
        ChessTrajectoryDecomposer.Deposit(b, witnessed, ChessVocabulary.TrajectorySourceId);
        return b.SetInputUnitsConsumed(1).Build();
    }

    [Fact]
    public void Backfill_DepositsNoTestimony()
    {
        Assert.Empty(ComposeBackfill().Attestations);
    }

    [Fact]
    public void Backfill_DepositsTheTrajectoryAndItsOwnMarker()
    {
        var change = ComposeBackfill();
        var lineId = ChessPgnDecomposer.TryParseGame(Game)!.LineId;

        var traj = Assert.Single(change.Physicalities,
            p => p.EntityId == lineId && p.Type == PhysicalityType.Projection);
        Assert.Equal(8, traj.NConstituents);
        Assert.Equal(Parsed().PositionIds, Trajectory.Constituents(traj.TrajectoryXyzm!));

        Assert.Contains(change.Entities, e => e.Id == ChessTrajectoryDecomposer.MarkerId(lineId));
        Assert.NotEqual(ChessTrajectoryDecomposer.MarkerId(lineId),
                        ChessVocabulary.AnalysisMarkerId(lineId, ChessAnalyze.Version));
    }

    [Fact]
    public void Backfill_MatchesWhatTheInlineAnalyzerDeposits()
    {
        var inline = LineTrajectory(Compose(), PhysicalityType.Projection);
        var backfilled = Assert.Single(ComposeBackfill().Physicalities,
            p => p.EntityId == inline.EntityId && p.Type == PhysicalityType.Projection);
        Assert.Equal(inline.TrajectoryXyzm, backfilled.TrajectoryXyzm);
        Assert.Equal(inline.Id, backfilled.Id);
        Assert.Equal(inline.NConstituents, backfilled.NConstituents);
    }
}
