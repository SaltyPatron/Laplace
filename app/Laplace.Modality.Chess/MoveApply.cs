namespace Laplace.Modality.Chess;

/// <summary>State needed to reverse a <see cref="MoveApply.Make"/> (make/unmake, no allocation).</summary>
public readonly record struct Undo(
    Piece CapturedPiece, int CapturedSquare,
    CastleRights Castle, int EpSquare, int HalfmoveClock, int FullmoveNumber);

/// <summary>
/// Mutating move application on a <see cref="Board"/>. Supports both copy-make (clone then
/// <see cref="Make(Board,ChessMove)"/>) and allocation-free make/unmake
/// (<see cref="MakeWithUndo"/> / <see cref="Unmake"/>). Updates piece placement, side to move,
/// castling rights, en-passant square, halfmove clock and fullmove number.
/// </summary>
public static class MoveApply
{
    public static void Make(Board b, ChessMove m) => MakeWithUndo(b, m);

    /// <summary>Apply <paramref name="m"/>, returning the info needed to <see cref="Unmake"/> it.</summary>
    public static Undo MakeWithUndo(Board b, ChessMove m)
    {
        bool white = b.WhiteToMove;
        Piece moving = b.Squares[m.From];
        bool isPawn = Board.TypeOf(moving) == Piece.WPawn;

        // Capture the pre-move reversible state for Unmake.
        var prevCastle = b.Castle;
        var prevEp = b.EpSquare;
        var prevHalf = b.HalfmoveClock;
        var prevFull = b.FullmoveNumber;

        Piece captured;
        int capturedSquare;
        if ((m.Flags & MoveFlags.EnPassant) != 0)
        {
            capturedSquare = white ? m.To - 16 : m.To + 16;
            captured = b.Squares[capturedSquare];
        }
        else
        {
            capturedSquare = m.To;
            captured = b.Squares[m.To];
        }
        bool isCapture = captured != Piece.Empty;

        // Move the piece.
        b.Squares[m.To] = moving;
        b.Squares[m.From] = Piece.Empty;

        // En-passant capture: remove the pawn behind the target.
        if ((m.Flags & MoveFlags.EnPassant) != 0)
            b.Squares[capturedSquare] = Piece.Empty;

        // Promotion: replace with promoted piece (correct colour).
        if (m.IsPromotion)
        {
            Piece promo = white ? m.Promotion : (Piece)(-(sbyte)Board.TypeOf(m.Promotion));
            b.Squares[m.To] = promo;
        }

        // Castling: move the rook.
        if ((m.Flags & MoveFlags.CastleKing) != 0)
        {
            int rank = white ? 0 : 7;
            b.Squares[Board.Sq(5, rank)] = b.Squares[Board.Sq(7, rank)];
            b.Squares[Board.Sq(7, rank)] = Piece.Empty;
        }
        else if ((m.Flags & MoveFlags.CastleQueen) != 0)
        {
            int rank = white ? 0 : 7;
            b.Squares[Board.Sq(3, rank)] = b.Squares[Board.Sq(0, rank)];
            b.Squares[Board.Sq(0, rank)] = Piece.Empty;
        }

        // Update castling rights: any move from/to a rook or king square clears the relevant right.
        UpdateCastleRights(b, m.From);
        UpdateCastleRights(b, m.To);

        // En-passant square: only set on a double push.
        b.EpSquare = (m.Flags & MoveFlags.DoublePush) != 0
            ? (white ? m.From + 16 : m.From - 16)
            : -1;

        // Halfmove clock.
        b.HalfmoveClock = (isPawn || isCapture) ? 0 : b.HalfmoveClock + 1;

        // Fullmove number increments after Black moves.
        if (!white) b.FullmoveNumber++;

        b.WhiteToMove = !white;

        return new Undo(captured, capturedSquare, prevCastle, prevEp, prevHalf, prevFull);
    }

    /// <summary>Reverse a <see cref="MakeWithUndo"/>. <paramref name="m"/> must be the move that was made.</summary>
    public static void Unmake(Board b, ChessMove m, in Undo u)
    {
        // Side that made the move (currently it's the opponent's turn).
        bool white = !b.WhiteToMove;

        // The piece sitting on m.To is the moved piece (or a promoted piece).
        Piece moved = b.Squares[m.To];

        // Undo promotion: the piece that left m.From was a pawn.
        if (m.IsPromotion)
            moved = white ? Piece.WPawn : Piece.BPawn;

        b.Squares[m.From] = moved;
        b.Squares[m.To] = Piece.Empty;

        // Restore a captured piece (en-passant restores on a different square).
        if (u.CapturedPiece != Piece.Empty)
            b.Squares[u.CapturedSquare] = u.CapturedPiece;

        // Undo castling rook move.
        if ((m.Flags & MoveFlags.CastleKing) != 0)
        {
            int rank = white ? 0 : 7;
            b.Squares[Board.Sq(7, rank)] = b.Squares[Board.Sq(5, rank)];
            b.Squares[Board.Sq(5, rank)] = Piece.Empty;
        }
        else if ((m.Flags & MoveFlags.CastleQueen) != 0)
        {
            int rank = white ? 0 : 7;
            b.Squares[Board.Sq(0, rank)] = b.Squares[Board.Sq(3, rank)];
            b.Squares[Board.Sq(3, rank)] = Piece.Empty;
        }

        b.Castle = u.Castle;
        b.EpSquare = u.EpSquare;
        b.HalfmoveClock = u.HalfmoveClock;
        b.FullmoveNumber = u.FullmoveNumber;
        b.WhiteToMove = white;
    }

    private static void UpdateCastleRights(Board b, int sq)
    {
        switch (sq)
        {
            case 4:  b.Castle &= ~(CastleRights.WhiteKing | CastleRights.WhiteQueen); break; // e1
            case 0:  b.Castle &= ~CastleRights.WhiteQueen; break; // a1
            case 7:  b.Castle &= ~CastleRights.WhiteKing; break;  // h1
            case 116: b.Castle &= ~(CastleRights.BlackKing | CastleRights.BlackQueen); break; // e8 = 7*16+4
            case 112: b.Castle &= ~CastleRights.BlackQueen; break; // a8
            case 119: b.Castle &= ~CastleRights.BlackKing; break;  // h8
        }
    }
}
