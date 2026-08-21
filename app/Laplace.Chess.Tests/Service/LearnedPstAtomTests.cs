using System.Linq;
using Laplace.Chess.Service;
using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Chess.Tests.Service;

/// <summary>
/// /chess/learned-pst returned HTTP 500 "Unexpected endpoint failure" on EVERY call.
/// LearnedPst.ReadWhite built a token like "Pa1" and passed it to ChessCompose.Position(string),
/// which requires a canonical "stm: cr: ep: ..." interchange surface; TryFenFromSurface rejected
/// it and the ArgumentException escaped on the very first of 384 squares.
///
/// The reader now asks for the piece-square constituent directly. What these tests pin is that
/// the id it gets back is the SAME id the compose path deposits for that square -- otherwise the
/// grid would stop throwing and start reading edges that nothing ever writes, which is worse.
/// </summary>
public sealed class LearnedPstAtomTests
{
    [Theory]
    [InlineData("P", 0, 0)]   // a1
    [InlineData("N", 6, 0)]   // g1
    [InlineData("K", 4, 0)]   // e1
    [InlineData("Q", 3, 7)]   // d8
    public void PieceSquareNode_IsTheSubstructureComposeDeposits(string piece, int file, int rank)
    {
        char pc = piece[0];
        string square = $"{(char)('a' + file)}{(char)('1' + rank)}";
        var board = Board.FromFen(OnlyPieceFen(pc, file, rank));

        var composed = ChessCompose.Position(board);
        var direct = ChessCompose.PieceSquareNode(WhitePiece(pc), file, rank);

        Assert.True(
            composed.Substructures.Any(s => s.Id.Equals(direct.Id)),
            $"{pc} on {square}: PieceSquareNode id is not among the substructures "
            + "ChessCompose.Position deposits for the same board");
    }

    /// <summary>
    /// The header atoms come FIRST. Substructures[0] -- what the broken reader reached for --
    /// is side-to-move, never a piece-square, so that index could not have been right either.
    /// </summary>
    [Fact]
    public void SubstructureZero_IsNotAPieceSquare()
    {
        var composed = ChessCompose.Position(Board.FromFen(OnlyPieceFen('P', 0, 0)));
        var pawnA1 = ChessCompose.PieceSquareNode(Piece.WPawn, 0, 0);
        Assert.NotEqual(pawnA1.Id, composed.Substructures[0].Id);
    }

    [Fact]
    public void EveryWhitePieceAndSquare_Composes()
    {
        // 6 x 64 = the 384 edges the panel claims to query. The old path threw on the first one.
        int n = 0;
        foreach (char pc in LearnedPst.WhitePieces)
            for (int rank = 0; rank < 8; rank++)
                for (int file = 0; file < 8; file++)
                {
                    var node = ChessCompose.PieceSquareNode(WhitePiece(pc), file, rank);
                    Assert.NotEqual(default, node.Id);
                    n++;
                }
        Assert.Equal(384, n);
    }

    private static string OnlyPieceFen(char pc, int file, int rank)
    {
        var board = new char[8, 8];
        board[rank, file] = pc;
        // Two kings so the FEN is a position, not a fragment. Placed off the tested square.
        if (!(pc == 'K' && file == 4 && rank == 0)) board[0, 4] = 'K';
        board[7, 7] = 'k';
        if (pc == 'Q' && file == 3 && rank == 7) board[7, 7] = 'k';

        var rows = new System.Text.StringBuilder();
        for (int r = 7; r >= 0; r--)
        {
            int empty = 0;
            for (int f = 0; f < 8; f++)
            {
                char c = board[r, f];
                if (c == '\0') { empty++; continue; }
                if (empty > 0) { rows.Append(empty); empty = 0; }
                rows.Append(c);
            }
            if (empty > 0) rows.Append(empty);
            if (r > 0) rows.Append('/');
        }
        return rows + " w - - 0 1";
    }

    private static Piece WhitePiece(char c) => c switch
    {
        'P' => Piece.WPawn,
        'N' => Piece.WKnight,
        'B' => Piece.WBishop,
        'R' => Piece.WRook,
        'Q' => Piece.WQueen,
        'K' => Piece.WKing,
        _ => throw new System.ArgumentOutOfRangeException(nameof(c)),
    };
}
