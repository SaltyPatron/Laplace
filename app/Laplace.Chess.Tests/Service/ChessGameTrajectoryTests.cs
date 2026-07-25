using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// The GAME TRAJECTORY (spec 11 §2): one GeometryZM linestring per game whose vertices are the
/// positions it passed through, with the position ids bit-packed into the mantissa channel.
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

    /// <summary>The position sequence this game walks, composed the way the analyzer composes it.</summary>
    private static List<Hash128> ExpectedLine()
    {
        var m = new ChessModality();
        var state = m.Initial();
        var line = new List<Hash128>();
        lock (ChessCompose.Gate)
        {
            line.Add(ChessCompose.PositionId(m.StateKey(state)));
            foreach (var san in new[] { "e4", "e5", "Qh5", "Nc6", "Bc4", "Nf6", "Qxf7#" })
            {
                var mv = San.Resolve(state.Board, m.LegalActions(state), san);
                Assert.NotNull(mv);
                state = m.Apply(state, mv!.Value);
                line.Add(ChessCompose.PositionId(m.StateKey(state)));
            }
        }
        return line;
    }

    private static PhysicalityRow GameTrajectory(SubstrateChange change)
    {
        var gameEntity = Assert.Single(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        return Assert.Single(change.Physicalities, p => p.EntityId == gameEntity.Id);
    }

    [Fact]
    public void Game_CarriesATrajectory()
    {
        var traj = GameTrajectory(Compose());
        Assert.NotNull(traj.TrajectoryXyzm);
        Assert.NotEmpty(traj.TrajectoryXyzm!);
    }

    [Fact]
    public void Trajectory_HasOneVertexPerBoardIncludingTheStart()
    {
        var traj = GameTrajectory(Compose());
        // 7 plies means 8 boards: the starting position plus one after each move. Dropping the
        // start would make the first move unrecoverable from the line alone.
        Assert.Equal(8, traj.NConstituents);
        Assert.Equal(8 * 4, traj.TrajectoryXyzm!.Length);
    }

    [Fact]
    public void Trajectory_InvertsBackToTheExactPositionSequence()
    {
        var traj = GameTrajectory(Compose());
        var recovered = Trajectory.Constituents(traj.TrajectoryXyzm!);
        Assert.Equal(ExpectedLine(), recovered);
    }

    [Fact]
    public void Trajectory_LandsOnPositionsTheSameChangeDeposited()
    {
        var change = Compose();
        var recovered = Trajectory.Constituents(GameTrajectory(change).TrajectoryXyzm!);
        var deposited = change.Entities
            .Where(e => e.TypeId == ChessVocabulary.PositionType)
            .Select(e => e.Id)
            .ToHashSet();

        // Every vertex is a position entity this very change emitted — the line indexes the
        // resident graph, it does not describe boards that live nowhere.
        Assert.All(recovered, id => Assert.Contains(id, deposited));
    }

    [Fact]
    public void Trajectory_IsAPhysicalityNotPartOfTheGameId()
    {
        var change = Compose();
        var gameEntity = Assert.Single(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        // The game's id is content-addressed from provenance + line. Geometry is identity and
        // reconstruction, never semantics, and must never leak into the hash — otherwise
        // re-deriving geometry would mint a different game.
        Assert.Equal(gameEntity.Id, ChessVocabulary.GameId("Alice", "Bob", "2024.01.01",
            ["e4", "e5", "Qh5", "Nc6", "Bc4", "Nf6", "Qxf7#"]));
    }

    [Fact]
    public void Trajectory_IsDeterministic()
    {
        // Same game, composed twice: byte-identical geometry. A trajectory that drifted would
        // break the content-addressed rock the same way non-deterministic codegen would.
        var a = GameTrajectory(Compose());
        var b = GameTrajectory(Compose());
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.TrajectoryXyzm, b.TrajectoryXyzm);
        Assert.Equal(a.CoordX, b.CoordX);
        Assert.Equal(a.CoordY, b.CoordY);
        Assert.Equal(a.CoordZ, b.CoordZ);
        Assert.Equal(a.CoordM, b.CoordM);
    }

    [Fact]
    public void NoAnalyze_DepositsNoTrajectory()
    {
        // The trajectory is the CALCULATED layer. --no-analyze must leave the pure witnessed
        // record: the game, its tags, its verbatim movetext, and no derived geometry.
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/pgn");
        ChessPgnDecomposer.ComposeGame(parsed, b, analyzeInline: false);
        var change = b.SetInputUnitsConsumed(1).Build();

        var gameEntity = Assert.Single(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        Assert.DoesNotContain(change.Physicalities, p => p.EntityId == gameEntity.Id);
    }
}
