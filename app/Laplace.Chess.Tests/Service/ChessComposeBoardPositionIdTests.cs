using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Board→id must stay bit-identical to Surface→id. Lookup/allocation change only.
/// </summary>
public sealed class ChessComposeBoardPositionIdTests
{
    [Fact]
    public void BoardPositionId_MatchesSurfacePath_StartAndPlies()
    {
        CodepointPerfcache.LoadDefault();
        var m = new ChessModality();
        lock (ChessCompose.Gate)
        {
            var state = m.Initial();
            AssertEqual(state);

            foreach (var san in new[] { "e4", "e5", "Nf3", "Nc6", "Bb5" })
            {
                var mv = San.Resolve(state.Board, m.LegalActions(state), san);
                Assert.NotNull(mv);
                state = m.Apply(state, mv!.Value);
                AssertEqual(state);
            }
        }
    }

    private static void AssertEqual(ChessState state)
    {
        string surface = state.Board is var b
            ? PositionContent.Surface(b, Ep(b))
            : throw new InvalidOperationException();
        Assert.Equal(ChessCompose.PositionId(surface), ChessCompose.PositionId(state.Board));
    }

    private static string Ep(Board b)
    {
        int ep = ChessModality.CapturableEpSquare(b);
        return ep < 0 ? "-" : Board.SquareToAlgebraic(ep);
    }
}
