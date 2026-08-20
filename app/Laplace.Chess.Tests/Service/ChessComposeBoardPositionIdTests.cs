using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Interchange text and the binary board path must resolve to one typed position composition.
/// </summary>
public sealed class ChessComposeBoardPositionIdTests
{
    [Fact]
    public void BoardPositionId_MatchesSurfacePath_StartAndPlies()
    {
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

    [Fact]
    public void PositionPhysicality_IsTypedBoardTrajectory_NotTextSentence()
    {
        var board = Board.FromFen(ChessModality.StartFen);
        var composed = ChessCompose.Position(board);

        Assert.Equal(35, composed.Position.NConstituents); // 3 state atoms + 32 pieces
        Assert.Equal(composed.Substructures.Select(static n => n.Id).ToArray(),
            Trajectory.Constituents(composed.Position.Trajectory).ToArray());
        Assert.All(composed.Substructures, atom =>
            Assert.Equal(5, atom.NConstituents)); // domain + two tagged nibbles per ushort
        Assert.Equal(0x0F, ChessPositionIdentity.CastlingDestinationMask(board));
    }

    [Fact]
    public void Chess960_UsesTheSameFourCastlingDestinationBits()
    {
        var board = Board.FromFen(
            "nqrkbbrn/pppppppp/8/8/8/8/PPPPPPPP/NQRKBBRN w GCgc - 0 1");
        Assert.Equal(0x0F, ChessPositionIdentity.CastlingDestinationMask(board));
        Assert.Equal(35, ChessCompose.Position(board).Position.NConstituents);
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
