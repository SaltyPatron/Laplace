namespace Laplace.Modality.Chess;

/// <summary>
/// Legal move generation on a 0x88 board using copy-make. Pseudo-legal moves are generated then
/// filtered by making the move and checking the mover's king is not attacked. Castling legality
/// (squares not attacked, not in check) is enforced at generation time.
/// </summary>
public static class MoveGen
{
    // 0x88 offsets.
    private static readonly int[] KnightDeltas;
    private static readonly int[] KingDeltas;
    private static readonly int[] BishopDeltas;
    private static readonly int[] RookDeltas;
    private static readonly int[] QueenDeltas;
    private static readonly int[] WPawnCaps;
    private static readonly int[] BPawnCaps;

    // Explicit static constructor (NOT beforefieldinit): guarantees these arrays are fully
    // initialized and published with a memory barrier before any thread reads them, which matters
    // when xUnit runs the heavy perft facts in parallel from a cold process.
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

    /// <summary>True if <paramref name="sq"/> is attacked by the side given (byWhite = attacker is white).</summary>
    public static bool IsSquareAttacked(Board b, int sq, bool byWhite)
    {
        // Pawn attacks: a white pawn on s attacks s+15 and s+17 (toward higher ranks).
        // So sq is attacked by a white pawn located at sq-15 or sq-17.
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

        // Sliding: bishops/queens on diagonals, rooks/queens on orthogonals.
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

    /// <summary>All fully legal moves for the side to move.</summary>
    public static List<ChessMove> Legal(Board b)
    {
        var pseudo = Pseudo(b);
        var legal = new List<ChessMove>(pseudo.Count);
        bool mover = b.WhiteToMove;
        foreach (var m in pseudo)
        {
            var undo = MoveApply.MakeWithUndo(b, m);
            // After Make, side has flipped; the mover's king must not be attacked.
            if (!InCheck(b, mover))
                legal.Add(m);
            MoveApply.Unmake(b, m, undo);
        }
        return legal;
    }

    /// <summary>Pseudo-legal moves (castling already legality-checked for attacks/check).</summary>
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
                // double push
                if (Board.RankOf(from) == startRank)
                {
                    int two = one + dir;
                    if (b.Squares[two] == Piece.Empty)
                        moves.Add(new ChessMove(from, two, Piece.Empty, MoveFlags.DoublePush));
                }
            }
        }

        // captures
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
        // King must be on its home square and not in check; squares between empty & not attacked.
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
