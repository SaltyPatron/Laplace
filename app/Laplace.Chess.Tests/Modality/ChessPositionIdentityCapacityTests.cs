using Laplace.Chess.Service;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

/// <summary>
/// The atom buffer must be sized by the BOARD (64 squares), not by the rules of legal chess
/// (32 pieces). FillAtoms emits one atom per occupied square and hashes whatever board it is
/// handed; it does not get to assume the position is legal.
/// </summary>
public sealed class ChessPositionIdentityCapacityTests
{
    /// <summary>
    /// The exact FEN that killed two ingest units. Chess.com "Odds Chess" gives one side three
    /// full ranks of pawns: 41 occupied squares against a 40-slot stackalloc. PositionId threw
    /// IndexOutOfRangeException up through ChessModality.FromFen and TryParseGame, which does
    /// not catch it, so the ENTIRE FILE failed -- Firouzja2003_chesscom.pgn and
    /// Hikaru_chesscom.pgn, one such game each, seed runs 32438771887 and 32439795126.
    /// </summary>
    public const string OddsChessFen =
        "rnbqkbnr/pppppppp/8/8/PPPPPPPP/PPPPPPPP/PPPPPPPP/4K3 w kq - 0 1";

    /// <summary>Every square occupied: the actual worst case the buffer must seat.</summary>
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
        // ChessCompose.Position held its OWN copy of the magic 40. A bound that lives in two
        // places is a bound that gets fixed in one of them.
        var composed = ChessCompose.Position(Board.FromFen(fen));
        Assert.NotEqual(default, composed.Position.Id);
    }

    [Fact]
    public void MaxAtoms_CoversHeaderPlusEverySquare()
    {
        Assert.Equal(ChessPositionIdentity.MaxHeaderAtoms + 64, ChessPositionIdentity.MaxAtoms);
    }
}
