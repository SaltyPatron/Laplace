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

        b.Squares[m.To] = moving;
        b.Squares[m.From] = Piece.Empty;

        if ((m.Flags & MoveFlags.EnPassant) != 0)
            b.Squares[capturedSquare] = Piece.Empty;

        if (m.IsPromotion)
        {
            Piece promo = white ? m.Promotion : (Piece)(-(sbyte)Board.TypeOf(m.Promotion));
            b.Squares[m.To] = promo;
        }

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

        UpdateCastleRights(b, m.From);
        UpdateCastleRights(b, m.To);

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

        Piece moved = b.Squares[m.To];

        if (m.IsPromotion)
            moved = white ? Piece.WPawn : Piece.BPawn;

        b.Squares[m.From] = moved;
        b.Squares[m.To] = Piece.Empty;

        if (u.CapturedPiece != Piece.Empty)
            b.Squares[u.CapturedSquare] = u.CapturedPiece;

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
            case 4:  b.Castle &= ~(CastleRights.WhiteKing | CastleRights.WhiteQueen); break;
            case 0:  b.Castle &= ~CastleRights.WhiteQueen; break;
            case 7:  b.Castle &= ~CastleRights.WhiteKing; break;
            case 116: b.Castle &= ~(CastleRights.BlackKing | CastleRights.BlackQueen); break;
            case 112: b.Castle &= ~CastleRights.BlackQueen; break;
            case 119: b.Castle &= ~CastleRights.BlackKing; break;
        }
    }
}
