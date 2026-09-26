using Laplace.Chess.Service;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

/// <summary>
/// The atom buffer is sized by the board (64 squares), not by legal chess (32 pieces):
/// FillAtoms emits one atom per occupied square of whatever board it is given.
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

        var id = ChessPositionIdentity.PositionId(board);
        Assert.NotEqual(default, id);
    }

    [Theory]
    [InlineData(OddsChessFen)]
    [InlineData(FullBoardFen)]
    public void Compose_UsesTheSameBoundAsPositionId(string fen)
    {
        // ChessCompose.Position uses the same atom bound as PositionId.
        var composed = ChessCompose.Position(Board.FromFen(fen));
        Assert.NotEqual(default, composed.Position.Id);
    }

    [Fact]
    public void MaxAtoms_CoversHeaderPlusEverySquare()
    {
        Assert.Equal(ChessPositionIdentity.MaxHeaderAtoms + 64, ChessPositionIdentity.MaxAtoms);
    }
}
