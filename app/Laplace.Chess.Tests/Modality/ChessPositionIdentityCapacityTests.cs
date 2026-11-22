using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

/// <summary>
/// Engine position identity handles boards with more occupied squares than legal chess (64 squares)
/// as well as full variant state without fixed buffer overflows.
/// </summary>
public sealed class ChessPositionIdentityCapacityTests
{
    /// <summary>
    /// A chess.com "Odds Chess" start: one side has three full ranks of pawns, 41 occupied
    /// squares, more than a legal position can hold.
    /// </summary>
    public const string OddsChessFen =
        "rnbqkbnr/pppppppp/8/8/PPPPPPPP/PPPPPPPP/PPPPPPPP/4K3 w kq - 0 1";

    /// <summary>Every square occupied: the largest board the buffer must hold.</summary>
    public const string FullBoardFen =
        "rnbqkbnr/pppppppp/pppppppp/pppppppp/PPPPPPPP/PPPPPPPP/PPPPPPPP/RNBQKBNR w - - 0 1";

    [Theory]
    [InlineData(OddsChessFen, 41)]
    [InlineData(FullBoardFen, 64)]
    public void PositionId_SurvivesMoreOccupiedSquaresThanLegalChessAllows(string fen, int occupied)
    {
        var board = Board.FromFen(fen);
        Assert.Equal(occupied, Bitboards.Count(board.CopyBitboards().Occupied));

        var id = ChessEngineIdentity.PositionId(board);
        Assert.NotEqual(default, id);
    }

    [Fact]
    public void PositionId_HandlesFullBoardWithVariantExtras()
    {
        var board = Board.FromFen(FullBoardFen, ChessVariants.Crazyhouse);
        board.Promoted = ulong.MaxValue;
        board.Pockets[0] = 64;
        board.Checks[0] = 2;
        board.Checks[1] = 1;
        Assert.NotEqual(default, ChessEngineIdentity.PositionId(board));
    }
}
