namespace Laplace.Modality.Chess;

public static class MoveGen
{
    private static readonly int[] KnightDeltas;
    private static readonly int[] KingDeltas;
    private static readonly int[] BishopDeltas;
    private static readonly int[] RookDeltas;
    private static readonly int[] QueenDeltas;
    private static readonly int[] WPawnCaps;
    private static readonly int[] BPawnCaps;

    static MoveGen()
    {
        KnightDeltas = new[] { 33, 31, 18, 14, -33, -31, -18, -14 };
        KingDeltas = new[] { 16, -16, 1, -1, 17, 15, -17, -15 };
        BishopDeltas = new[] { 17, 15, -17, -15 };
        RookDeltas = new[] { 16, -16, 1, -1 };
        QueenDeltas = new[] { 16, -16, 1, -1, 17, 15, -17, -15 };
        WPawnCaps = new[] { 15, 17 };
        BPawnCaps = new[] { -15, -17 };
    }

    public static bool IsSquareAttacked(Board b, int sq, bool byWhite)
    {
        if (byWhite)
        {
            int p1 = sq - 17, p2 = sq - 15;
            if (Board.OnBoard(p1) && b.Squares[p1] == Piece.WPawn) return true;
            if (Board.OnBoard(p2) && b.Squares[p2] == Piece.WPawn) return true;
        }
        else
        {
            int p1 = sq + 17, p2 = sq + 15;
            if (Board.OnBoard(p1) && b.Squares[p1] == Piece.BPawn) return true;
            if (Board.OnBoard(p2) && b.Squares[p2] == Piece.BPawn) return true;
        }

        Piece knight = byWhite ? Piece.WKnight : Piece.BKnight;
        foreach (int d in KnightDeltas)
        {
            int t = sq + d;
            if (Board.OnBoard(t) && b.Squares[t] == knight) return true;
        }

        Piece king = byWhite ? Piece.WKing : Piece.BKing;
        foreach (int d in KingDeltas)
        {
            int t = sq + d;
            if (Board.OnBoard(t) && b.Squares[t] == king) return true;
        }

        Piece bishop = byWhite ? Piece.WBishop : Piece.BBishop;
        Piece rook = byWhite ? Piece.WRook : Piece.BRook;
        Piece queen = byWhite ? Piece.WQueen : Piece.BQueen;

        foreach (int d in BishopDeltas)
        {
            int t = sq + d;
            while (Board.OnBoard(t))
            {
                var pc = b.Squares[t];
                if (pc != Piece.Empty)
                {
                    if (pc == bishop || pc == queen) return true;
                    break;
                }
                t += d;
            }
        }
        foreach (int d in RookDeltas)
        {
            int t = sq + d;
            while (Board.OnBoard(t))
            {
                var pc = b.Squares[t];
                if (pc != Piece.Empty)
                {
                    if (pc == rook || pc == queen) return true;
                    break;
                }
                t += d;
            }
        }
        return false;
    }

    public static bool InCheck(Board b, bool whiteKing)
    {
        int k = b.FindKing(whiteKing);
        if (k < 0) return false;
        return IsSquareAttacked(b, k, byWhite: !whiteKing);
    }

    /// Squares of enemy pieces directly attacked by the piece on `from` (sliding rays stop at the
    /// first occupied square, same as real capture rules). Used for tactical-motif detection
    /// (forks, hanging pieces) — a "from the piece's own square outward" counterpart to
    /// IsSquareAttacked's "is this square attacked by any piece of color X" check.
    public static List<int> EnemyPiecesAttackedFrom(Board b, int from)
    {
        var result = new List<int>();
        var piece = b.Squares[from];
        if (piece == Piece.Empty) return result;
        bool white = Board.IsWhite(piece);

        void MaybeAdd(int t)
        {
            if (!Board.OnBoard(t)) return;
            var pc = b.Squares[t];
            if (pc != Piece.Empty && Board.IsWhite(pc) != white) result.Add(t);
        }

        switch (Board.TypeOf(piece))
        {
            case Piece.WPawn:
                foreach (int d in white ? WPawnCaps : BPawnCaps) MaybeAdd(from + d);
                break;
            case Piece.WKnight:
                foreach (int d in KnightDeltas) MaybeAdd(from + d);
                break;
            case Piece.WKing:
                foreach (int d in KingDeltas) MaybeAdd(from + d);
                break;
            case Piece.WBishop:
            case Piece.WRook:
            case Piece.WQueen:
                var deltas = Board.TypeOf(piece) == Piece.WBishop ? BishopDeltas
                    : Board.TypeOf(piece) == Piece.WRook ? RookDeltas : QueenDeltas;
                foreach (int d in deltas)
                {
                    int t = from + d;
                    while (Board.OnBoard(t))
                    {
                        var pc = b.Squares[t];
                        if (pc != Piece.Empty)
                        {
                            if (Board.IsWhite(pc) != white) result.Add(t);
                            break;
                        }
                        t += d;
                    }
                }
                break;
        }
        return result;
    }

    public static List<ChessMove> Legal(Board b)
    {
        var pseudo = Pseudo(b);
        var legal = new List<ChessMove>(pseudo.Count);
        bool mover = b.WhiteToMove;
        foreach (var m in pseudo)
        {
            var undo = MoveApply.MakeWithUndo(b, m);
            if (!InCheck(b, mover))
                legal.Add(m);
            MoveApply.Unmake(b, m, undo);
        }
        return legal;
    }

    public static List<ChessMove> Pseudo(Board b)
    {
        var moves = new List<ChessMove>(64);
        bool white = b.WhiteToMove;

        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = b.Squares[sq];
            if (p == Piece.Empty) continue;
            if (Board.IsWhite(p) != white) continue;

            switch (Board.TypeOf(p))
            {
                case Piece.WPawn: GenPawn(b, sq, white, moves); break;
                case Piece.WKnight: GenLeaper(b, sq, white, KnightDeltas, moves); break;
                case Piece.WBishop: GenSlider(b, sq, white, BishopDeltas, moves); break;
                case Piece.WRook: GenSlider(b, sq, white, RookDeltas, moves); break;
                case Piece.WQueen: GenSlider(b, sq, white, QueenDeltas, moves); break;
                case Piece.WKing: GenLeaper(b, sq, white, KingDeltas, moves); GenCastle(b, sq, white, moves); break;
            }
        }
        return moves;
    }

    private static void GenLeaper(Board b, int from, bool white, int[] deltas, List<ChessMove> moves)
    {
        foreach (int d in deltas)
        {
            int to = from + d;
            if (!Board.OnBoard(to)) continue;
            var target = b.Squares[to];
            if (target == Piece.Empty || Board.IsWhite(target) != white)
                moves.Add(new ChessMove(from, to, Piece.Empty, MoveFlags.None));
        }
    }

    private static void GenSlider(Board b, int from, bool white, int[] deltas, List<ChessMove> moves)
    {
        foreach (int d in deltas)
        {
            int to = from + d;
            while (Board.OnBoard(to))
            {
                var target = b.Squares[to];
                if (target == Piece.Empty)
                {
                    moves.Add(new ChessMove(from, to, Piece.Empty, MoveFlags.None));
                }
                else
                {
                    if (Board.IsWhite(target) != white)
                        moves.Add(new ChessMove(from, to, Piece.Empty, MoveFlags.None));
                    break;
                }
                to += d;
            }
        }
    }

    private static void GenPawn(Board b, int from, bool white, List<ChessMove> moves)
    {
        int dir = white ? 16 : -16;
        int startRank = white ? 1 : 6;
        int promoRank = white ? 7 : 0;

        int one = from + dir;
        if (Board.OnBoard(one) && b.Squares[one] == Piece.Empty)
        {
            if (Board.RankOf(one) == promoRank)
                AddPromotions(from, one, white, moves);
            else
            {
                moves.Add(new ChessMove(from, one, Piece.Empty, MoveFlags.None));
                if (Board.RankOf(from) == startRank)
                {
                    int two = one + dir;
                    if (b.Squares[two] == Piece.Empty)
                        moves.Add(new ChessMove(from, two, Piece.Empty, MoveFlags.DoublePush));
                }
            }
        }

        foreach (int cd in white ? WPawnCaps : BPawnCaps)
        {
            int to = from + cd;
            if (!Board.OnBoard(to)) continue;
            var target = b.Squares[to];
            if (target != Piece.Empty && Board.IsWhite(target) != white)
            {
                if (Board.RankOf(to) == promoRank)
                    AddPromotions(from, to, white, moves);
                else
                    moves.Add(new ChessMove(from, to, Piece.Empty, MoveFlags.None));
            }
            else if (target == Piece.Empty && to == b.EpSquare)
            {
                moves.Add(new ChessMove(from, to, Piece.Empty, MoveFlags.EnPassant));
            }
        }
    }

    private static void AddPromotions(int from, int to, bool white, List<ChessMove> moves)
    {
        Piece q = white ? Piece.WQueen : Piece.BQueen;
        Piece r = white ? Piece.WRook : Piece.BRook;
        Piece bp = white ? Piece.WBishop : Piece.BBishop;
        Piece n = white ? Piece.WKnight : Piece.BKnight;
        moves.Add(new ChessMove(from, to, q, MoveFlags.Promotion));
        moves.Add(new ChessMove(from, to, r, MoveFlags.Promotion));
        moves.Add(new ChessMove(from, to, bp, MoveFlags.Promotion));
        moves.Add(new ChessMove(from, to, n, MoveFlags.Promotion));
    }

    private static void GenCastle(Board b, int from, bool white, List<ChessMove> moves)
    {
        bool attackerWhite = !white;
        if (white)
        {
            if (from != Board.Sq(4, 0)) return;
            if (IsSquareAttacked(b, from, attackerWhite)) return;
            if ((b.Castle & CastleRights.WhiteKing) != 0)
            {
                if (b.Squares[Board.Sq(5, 0)] == Piece.Empty &&
                    b.Squares[Board.Sq(6, 0)] == Piece.Empty &&
                    b.Squares[Board.Sq(7, 0)] == Piece.WRook &&
                    !IsSquareAttacked(b, Board.Sq(5, 0), attackerWhite) &&
                    !IsSquareAttacked(b, Board.Sq(6, 0), attackerWhite))
                    moves.Add(new ChessMove(from, Board.Sq(6, 0), Piece.Empty, MoveFlags.CastleKing));
            }
            if ((b.Castle & CastleRights.WhiteQueen) != 0)
            {
                if (b.Squares[Board.Sq(3, 0)] == Piece.Empty &&
                    b.Squares[Board.Sq(2, 0)] == Piece.Empty &&
                    b.Squares[Board.Sq(1, 0)] == Piece.Empty &&
                    b.Squares[Board.Sq(0, 0)] == Piece.WRook &&
                    !IsSquareAttacked(b, Board.Sq(3, 0), attackerWhite) &&
                    !IsSquareAttacked(b, Board.Sq(2, 0), attackerWhite))
                    moves.Add(new ChessMove(from, Board.Sq(2, 0), Piece.Empty, MoveFlags.CastleQueen));
            }
        }
        else
        {
            if (from != Board.Sq(4, 7)) return;
            if (IsSquareAttacked(b, from, attackerWhite)) return;
            if ((b.Castle & CastleRights.BlackKing) != 0)
            {
                if (b.Squares[Board.Sq(5, 7)] == Piece.Empty &&
                    b.Squares[Board.Sq(6, 7)] == Piece.Empty &&
                    b.Squares[Board.Sq(7, 7)] == Piece.BRook &&
                    !IsSquareAttacked(b, Board.Sq(5, 7), attackerWhite) &&
                    !IsSquareAttacked(b, Board.Sq(6, 7), attackerWhite))
                    moves.Add(new ChessMove(from, Board.Sq(6, 7), Piece.Empty, MoveFlags.CastleKing));
            }
            if ((b.Castle & CastleRights.BlackQueen) != 0)
            {
                if (b.Squares[Board.Sq(3, 7)] == Piece.Empty &&
                    b.Squares[Board.Sq(2, 7)] == Piece.Empty &&
                    b.Squares[Board.Sq(1, 7)] == Piece.Empty &&
                    b.Squares[Board.Sq(0, 7)] == Piece.BRook &&
                    !IsSquareAttacked(b, Board.Sq(3, 7), attackerWhite) &&
                    !IsSquareAttacked(b, Board.Sq(2, 7), attackerWhite))
                    moves.Add(new ChessMove(from, Board.Sq(2, 7), Piece.Empty, MoveFlags.CastleQueen));
            }
        }
    }
}
