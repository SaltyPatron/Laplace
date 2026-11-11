using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// A mate in one is found by move generation alone; selection then skips its substrate reads.
public sealed class ChessImmediateMateTests
{
    private static readonly ChessModality Modality = new();

    [Theory]
    // Fool's mate: Black plays Qh4#.
    [InlineData("rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq g3 0 2", true)]
    // Scholar's mate: White plays Qxf7#.
    [InlineData("r1bqkb1r/pppp1ppp/2n2n2/4p2Q/2B1P3/8/PPPP1PPP/RNB1K1NR w KQkq - 4 4", true)]
    // Back-rank mate: Ra8#.
    [InlineData("6k1/5ppp/8/8/8/8/8/R5K1 w - - 0 1", true)]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", false)]
    // Bare kings: no move can end the game in the mover's favour.
    [InlineData("k7/8/1K6/8/8/8/8/8 w - - 0 1", false)]
    public void MateInOneIsFoundWithoutSearch(string fen, bool expected)
        => Assert.Equal(expected, ChessEngineService.HasImmediateMate(Modality, Modality.FromFen(fen)));
}
