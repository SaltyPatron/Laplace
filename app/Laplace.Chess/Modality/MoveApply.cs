namespace Laplace.Modality.Chess;

public readonly record struct Undo(
    Piece CapturedPiece, int CapturedSquare,
    CastleRights Castle, int EpSquare, int HalfmoveClock, int FullmoveNumber);

public static class MoveApply
{
    public static void Make(Board b, ChessMove m) => MakeWithUndo(b, m);

    public static Undo MakeWithUndo(Board b, ChessMove m)
    {
        bool white = b.WhiteToMove;
        Piece moving = b.Squares[m.From];
        bool isPawn = Board.TypeOf(moving) == Piece.WPawn;

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

        bool isCastle = (m.Flags & (MoveFlags.CastleKing | MoveFlags.CastleQueen)) != 0;
        if (isCastle)
        {
            // ATOMIC, and not via the generic mover. Chess960 breaks two assumptions the
            // generic path makes: the king can castle WITHOUT MOVING (king g1, rook h1 —
            // `Squares[To] = moving; Squares[From] = Empty` with To == From deletes it),
            // and the king's destination can hold the castling rook, which the generic
            // path would score as a capture of one's own piece. Clear both origins first,
            // then place both, so any overlap resolves correctly.
            captured = Piece.Empty;
            capturedSquare = m.To;
            isCapture = false;
            PlaceCastle(b, m, white, undo: false);
        }
        else
        {
            b.Squares[m.To] = moving;
            b.Squares[m.From] = Piece.Empty;
        }

        if ((m.Flags & MoveFlags.EnPassant) != 0)
            b.Squares[capturedSquare] = Piece.Empty;

        if (m.IsPromotion)
        {
            Piece promo = white ? m.Promotion : (Piece)(-(sbyte)Board.TypeOf(m.Promotion));
            b.Squares[m.To] = promo;
        }

        // The mover leaving a square that matters, and the CAPTURED piece on the square it
        // died on — a rook taken on its home square loses its owner's right. `captured` is
        // the piece that was there, not b.Squares[m.To], which by now holds the mover.
        ClearCastleRights(b, moving, m.From);
        ClearCastleRights(b, captured, capturedSquare);

        b.EpSquare = (m.Flags & MoveFlags.DoublePush) != 0
            ? (white ? m.From + 16 : m.From - 16)
            : -1;

        b.HalfmoveClock = (isPawn || isCapture) ? 0 : b.HalfmoveClock + 1;

        if (!white) b.FullmoveNumber++;

        b.WhiteToMove = !white;

        return new Undo(captured, capturedSquare, prevCastle, prevEp, prevHalf, prevFull);
    }

    public static void Unmake(Board b, ChessMove m, in Undo u)
    {
        bool white = !b.WhiteToMove;

        if ((m.Flags & (MoveFlags.CastleKing | MoveFlags.CastleQueen)) != 0)
        {
            PlaceCastle(b, m, white, undo: true);
        }
        else
        {
            Piece moved = b.Squares[m.To];
            if (m.IsPromotion)
                moved = white ? Piece.WPawn : Piece.BPawn;

            b.Squares[m.From] = moved;
            b.Squares[m.To] = Piece.Empty;

            if (u.CapturedPiece != Piece.Empty)
                b.Squares[u.CapturedSquare] = u.CapturedPiece;
        }

        b.Castle = u.Castle;
        b.EpSquare = u.EpSquare;
        b.HalfmoveClock = u.HalfmoveClock;
        b.FullmoveNumber = u.FullmoveNumber;
        b.WhiteToMove = white;
    }

    /// <summary>
    /// Put the castling king and rook on their squares (or back). Both origins are cleared
    /// before either destination is written, which is what makes the Chess960 overlap cases
    /// — king already on its destination, rook sitting on it — resolve instead of erasing a
    /// piece. Destinations are fixed (king g/c, rook f/d) in Chess960 exactly as in chess.
    /// </summary>
    private static void PlaceCastle(Board b, ChessMove m, bool white, bool undo)
    {
        int rank = white ? 0 : 7;
        bool kingSide = (m.Flags & MoveFlags.CastleKing) != 0;
        int kingFrom = m.From;
        int rookFrom = Board.Sq(b.CastleRookFile(white, kingSide), rank);
        int kingTo = Board.Sq(kingSide ? 6 : 2, rank);
        int rookTo = Board.Sq(kingSide ? 5 : 3, rank);
        Piece king = white ? Piece.WKing : Piece.BKing;
        Piece rook = white ? Piece.WRook : Piece.BRook;

        if (undo) { (kingFrom, kingTo) = (kingTo, kingFrom); (rookFrom, rookTo) = (rookTo, rookFrom); }

        b.Squares[kingFrom] = Piece.Empty;
        b.Squares[rookFrom] = Piece.Empty;
        b.Squares[kingTo] = king;
        b.Squares[rookTo] = rook;
    }

    /// <summary>
    /// Rights lost by a piece leaving, or being captured on, a square that matters.
    ///
    /// Keyed on the PIECE and the stored rook files, not on the literal squares 0/4/7/
    /// 112/116/119 this used to switch over. Those constants encode "the king starts on e1
    /// and the rooks on a1/h1", which Chess960 denies — a king starting on b1 would have
    /// kept its castling rights forever.
    /// </summary>
    private static void ClearCastleRights(Board b, Piece piece, int sq)
    {
        if (piece == Piece.Empty) return;
        var type = Board.TypeOf(piece);
        bool pieceIsWhite = (sbyte)piece > 0;

        if (type == Piece.WKing)
        {
            b.Castle &= pieceIsWhite
                ? ~(CastleRights.WhiteKing | CastleRights.WhiteQueen)
                : ~(CastleRights.BlackKing | CastleRights.BlackQueen);
            return;
        }
        if (type != Piece.WRook) return;

        int rank = Board.RankOf(sq), file = Board.FileOf(sq);
        if (pieceIsWhite && rank == 0)
        {
            if (file == b.WhiteKingRookFile) b.Castle &= ~CastleRights.WhiteKing;
            if (file == b.WhiteQueenRookFile) b.Castle &= ~CastleRights.WhiteQueen;
        }
        else if (!pieceIsWhite && rank == 7)
        {
            if (file == b.BlackKingRookFile) b.Castle &= ~CastleRights.BlackKing;
            if (file == b.BlackQueenRookFile) b.Castle &= ~CastleRights.BlackQueen;
        }
    }
}
