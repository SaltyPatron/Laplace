using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class SubstrateTransitionChooserTests
{
    [Fact]
    public void FusesExactMoveAndChildSignalsInOneForwardPass()
    {
        var modality = new ChessModality();
        var state = modality.Initial();
        var legal = modality.LegalActions(state);
        var exactFavorite = legal.Single(m => m.ToUci() == "e2e4");
        var fusedFavorite = legal.Single(m => m.ToUci() == "d2d4");
        Hash128 Child(ChessMove move) => ChessCompose.PositionId(modality.Apply(state, move).Board);
        Hash128 MoveId(ChessMove move) => ChessCompose.MoveId(state.Board.Squares[move.From], move);
        string fusedSurface = modality.StateKey(modality.Apply(state, fusedFavorite));
        int moveReads = 0, stateReads = 0;

        var chooser = new SubstrateTransitionChooser((_, _) =>
        [
            // chess.moves() returns display units. Exact evidence alone prefers e2e4.
            new(Child(exactFavorite), 1700d, 100d, 1),
            new(Child(fusedFavorite), 1600d, 100d, 1),
        ], ids =>
        {
            Interlocked.Increment(ref moveReads);
            return new Dictionary<Hash128, SubstrateTransitionChooser.Rating>
            {
                [MoveId(fusedFavorite)] = new(MoveId(fusedFavorite),
                    2_000_000_000_000d, 50_000_000_000d, 9),
            };
        }, surfaces =>
        {
            Interlocked.Increment(ref stateReads);
            return surfaces.Select(surface => surface == fusedSurface
                ? 1_000_000_000_000d
                : GlickoPriors.NeutralMu).ToArray();
        });

        var decision = chooser.ChooseDecision(state, new Random(1));

        Assert.Equal("d2d4", decision.Move.ToUci());
        Assert.True(decision.ExactTransition);
        Assert.True(decision.MovePhysicality);
        Assert.True(decision.ChildStructure);
        Assert.True(decision.Rated);
        Assert.Equal(1, moveReads);
        Assert.Equal(1, stateReads);
        Assert.Equal(1, chooser.Snapshot.Decisions);
        Assert.Equal(1, chooser.Snapshot.ExactTransitionSignals);
        Assert.Equal(1, chooser.Snapshot.MovePhysicalitySignals);
        Assert.Equal(1, chooser.Snapshot.ChildStructureSignals);
    }

    [Fact]
    public void CoalescesParallelReads_AndRefreshesOnlyAfterPositionIsObserved()
    {
        var state = new ChessModality().Initial();
        var modality = new ChessModality();
        var child = ChessCompose.PositionId(modality.Apply(state, modality.LegalActions(state)[0]).Board);
        int reads = 0;
        var chooser = new SubstrateTransitionChooser((_, _) =>
        {
            Interlocked.Increment(ref reads);
            Thread.Sleep(10);
            return [new(child, 1500d, 350d, 1)];
        });

        Parallel.For(0, 4, _ => chooser.Choose(state, new Random(1)));
        Assert.Equal(1, reads);

        int refreshedReads = 0;
        var refreshed = new SubstrateTransitionChooser((_, _) =>
        {
            Interlocked.Increment(ref refreshedReads);
            return [new(child, 1500d, 350d, 1)];
        });
        refreshed.Choose(state, new Random(2));
        refreshed.Choose(state, new Random(3));
        Assert.Equal(1, refreshedReads);

        ChessTransitionObservations.MarkObserved([ChessCompose.PositionId(state.Board)]);
        refreshed.Choose(state, new Random(4));
        Assert.Equal(2, refreshedReads);
    }

    [Fact]
    public void RefusesAnUnratedPositionAfterAllSubstrateBatchesAreRead()
    {
        var state = new ChessModality().Initial();
        int transitions = 0, moves = 0, structures = 0;
        var chooser = new SubstrateTransitionChooser(
            (_, _) => { transitions++; return []; },
            _ => { moves++; return new Dictionary<Hash128, SubstrateTransitionChooser.Rating>(); },
            surfaces =>
            {
                structures++;
                return Enumerable.Repeat(GlickoPriors.NeutralMu, surfaces.Count).ToArray();
            });

        Assert.Throws<UnratedSubstratePositionException>(
            () => chooser.ChooseDecision(state, new Random(1)));
        Assert.Equal((1, 1, 1), (transitions, moves, structures));
    }
}
