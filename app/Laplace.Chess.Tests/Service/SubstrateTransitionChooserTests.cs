using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class SubstrateTransitionChooserTests
{
    [Fact]
    public void ChoosesTheStrongestWitnessedLegalTransition_WithoutCallingFallback()
    {
        var modality = new ChessModality();
        var state = modality.Initial();
        var legal = modality.LegalActions(state);
        var weak = legal.Single(m => m.ToUci() == "e2e4");
        var strong = legal.Single(m => m.ToUci() == "d2d4");
        Hash128 Child(ChessMove move) => ChessCompose.PositionId(modality.Apply(state, move).Board);

        var chooser = new SubstrateTransitionChooser((_, _) =>
        [
            new(Child(weak), 1_600_000_000_000d, 80_000_000_000d, 100),
            new(Child(strong), 1_700_000_000_000d, 90_000_000_000d, 20),
        ]);
        bool fellBack = false;

        var selected = chooser.Choose(state, new Random(1), (s, r) =>
        {
            fellBack = true;
            return MatchRunner.RandomChooser(s, r);
        });

        Assert.Equal("d2d4", selected.ToUci());
        Assert.False(fellBack);
    }

    [Fact]
    public void CoalescesParallelReads_AndRefreshesOnlyAfterPositionIsObserved()
    {
        var state = new ChessModality().Initial();
        int reads = 0;
        var chooser = new SubstrateTransitionChooser((_, _) =>
        {
            Interlocked.Increment(ref reads);
            Thread.Sleep(10);
            return [new(Hash128.OfCanonical("unmatched-child"), 1, 1, 1)];
        });

        Parallel.For(0, 4, _ => chooser.Choose(
            state, new Random(1), static (s, _) => MoveGen.Legal(s.Board)[0]));
        Assert.Equal(1, reads);

        int emptyReads = 0;
        var empty = new SubstrateTransitionChooser((_, _) =>
        {
            Interlocked.Increment(ref emptyReads);
            return Array.Empty<SubstrateTransitionChooser.Rating>();
        });
        empty.Choose(state, new Random(2), static (s, _) => MoveGen.Legal(s.Board)[0]);
        empty.Choose(state, new Random(3), static (s, _) => MoveGen.Legal(s.Board)[0]);
        Assert.Equal(1, emptyReads);

        ChessTransitionObservations.MarkObserved([ChessCompose.PositionId(state.Board)]);
        empty.Choose(state, new Random(4), static (s, _) => MoveGen.Legal(s.Board)[0]);
        Assert.Equal(2, emptyReads);
    }
}
