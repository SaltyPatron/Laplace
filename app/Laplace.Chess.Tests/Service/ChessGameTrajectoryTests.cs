using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// A reusable LINE owns an irreducible content trajectory of typed moves. Its board walk is a
/// deterministic, evictable projection produced from the start position and transition floor.
///
/// The property that makes it worth depositing rather than recomputing is INVERTIBILITY — the
/// exact move sequence has to come back out of the geometry. A linestring that only preserved
/// the spatial path would be a lossy summary, and the substrate does not store lossy summaries
/// of things it already holds exactly.
/// </summary>
public sealed class ChessGameTrajectoryTests
{
    // Scholar's mate: short, forced, and every position distinct.
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
    public void ContentTrajectory_HasOneVertexPerMove()
    {
        var traj = LineTrajectory(Compose(), PhysicalityType.Content);
        Assert.Equal(7, traj.NConstituents);
        Assert.Equal(7 * 4, traj.TrajectoryXyzm!.Length);
    }

    [Fact]
    public void ContentTrajectory_InvertsBackToTheExactMoveSequence()
    {
        var parsed = Parsed();
        var traj = LineTrajectory(Compose(), PhysicalityType.Content);
        var recovered = Trajectory.Constituents(traj.TrajectoryXyzm!);
        Assert.Equal(parsed.MoveIds, recovered);
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

        // The compose-floor blob accelerates these exact identities and coordinates; it is not
        // their only storage. Every board in the line, including the terminal board, remains a
        // first-class content entity with the typed-atom trajectory needed to reconstruct it.
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
        // The line id is the Merkle over its start state and ordered typed move objects.
        // Geometry is identity and reconstruction, never semantics,
        // and must never leak into the hash — otherwise re-deriving geometry would mint a
        // different game. (Provenance — who/when — never enters either; it lives on the
        // event.)
        Assert.Equal(gameEntity.Id,
            ChessCompose.LineId(parsed.PositionIds[0], parsed.MoveIds));
    }

    [Fact]
    public void Trajectory_IsDeterministic()
    {
        // Same game, composed twice: byte-identical geometry. A trajectory that drifted would
        // break the content-addressed rock the same way non-deterministic codegen would.
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
    public void NoAnalyze_KeepsMoveTrajectoryButOmitsPositionProjection()
    {
        // The position-line trajectory is calculated. --no-analyze retains the witnessed
        // typed move trajectory but does not project the ordered board path onto the LINE.
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

    // --- the backfill pass -------------------------------------------------
    // Reaching the games recorded before trajectories existed must NOT go through a
    // ChessAnalyze.Version bump: attestation merge ACCUMULATES observation_count, so
    // re-deriving the standing corpus would double every witness count in the calculated
    // layer. The backfill therefore writes geometry ONLY. These pin that.

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

        // GH #736 source split (#508): the trajectory lane writes under its OWN source.
        var b = new SubstrateChangeBuilder(ChessVocabulary.TrajectorySourceId, "test/trajectory");
        ChessTrajectoryDecomposer.Deposit(b, witnessed, ChessVocabulary.TrajectorySourceId);
        return b.SetInputUnitsConsumed(1).Build();
    }

    [Fact]
    public void Backfill_DepositsNoTestimony()
    {
        // The whole safety argument in one assertion: not one attestation, so nothing can
        // double-count no matter how many times this runs over the standing corpus.
        Assert.Empty(ComposeBackfill().Attestations);
    }

    [Fact]
    public void Backfill_DepositsTheTrajectoryAndItsOwnMarker()
    {
        var change = ComposeBackfill();
        // GH #736: the linestring hangs on the LINE — one per line, however many playings.
        var lineId = ChessPgnDecomposer.TryParseGame(Game)!.LineId;

        var traj = Assert.Single(change.Physicalities,
            p => p.EntityId == lineId && p.Type == PhysicalityType.Projection);
        Assert.Equal(8, traj.NConstituents);
        Assert.Equal(Parsed().PositionIds, Trajectory.Constituents(traj.TrajectoryXyzm!));

        // Marker is versioned independently of ChessAnalyze.Version so a geometry backfill
        // and a testimony re-derive can never be mistaken for one another.
        Assert.Contains(change.Entities, e => e.Id == ChessTrajectoryDecomposer.MarkerId(lineId));
        Assert.NotEqual(ChessTrajectoryDecomposer.MarkerId(lineId),
                        ChessVocabulary.AnalysisMarkerId(lineId, ChessAnalyze.Version));
    }

    [Fact]
    public void Backfill_MatchesWhatTheInlineAnalyzerDeposits()
    {
        // Two roads to the same geometry: a fresh ingest derives it inline, the backfill
        // derives it from the hydrated record. They must agree exactly, or a game's geometry
        // would depend on which path reached it.
        var inline = LineTrajectory(Compose(), PhysicalityType.Projection);
        var backfilled = Assert.Single(ComposeBackfill().Physicalities,
            p => p.EntityId == inline.EntityId && p.Type == PhysicalityType.Projection);
        Assert.Equal(inline.TrajectoryXyzm, backfilled.TrajectoryXyzm);
        Assert.Equal(inline.Id, backfilled.Id);
        Assert.Equal(inline.NConstituents, backfilled.NConstituents);
    }
}
