using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// Interchange text and the binary board path must resolve to one typed position identity.
/// </summary>
public sealed class ChessPositionSurfaceIdentityTests
{
    [Fact]
    public void BoardPositionId_MatchesSurfacePath_StartAndPlies()
    {
        var m = new ChessModality();
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

    [Fact]
    public void Chess960_UsesTheSameFourCastlingDestinationBits()
    {
        var board = Board.FromFen(
            "nqrkbbrn/pppppppp/8/8/8/8/PPPPPPPP/NQRKBBRN w GCgc - 0 1");
        Assert.Equal(0x0F, ChessPositionIdentity.CastlingDestinationMask(board));
    }

    private static void AssertEqual(ChessState state)
    {
        var board = state.Board;
        string surface = PositionContent.Surface(board, Ep(board));
        Assert.True(PositionContent.TryBoardFromSurface(surface, out var parsed));
        Assert.Equal(ChessPositionIdentity.PositionId(parsed), ChessPositionIdentity.PositionId(board));
    }

    private static string Ep(Board b)
    {
        int ep = ChessModality.CapturableEpSquare(b);
        return ep < 0 ? "-" : Board.SquareToAlgebraic(ep);
    }
}
