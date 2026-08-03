namespace Laplace.Modality.Chess;

public static class MoveGen
{
    // Internal so See's least-valuable-attacker scan rides the same delta tables
    // (one implementation per fact).
    internal static readonly int[] KnightDeltas;
    internal static readonly int[] KingDeltas;
    internal static readonly int[] BishopDeltas;
    internal static readonly int[] RookDeltas;
    internal static readonly int[] QueenDeltas;
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
        => IsSquareAttacked(b.Squares, sq, byWhite);

    // Array form: the same fact computed over a raw square array, so See's swap-off can
    // probe hypothetical occupancies without materializing Board instances.
    public static bool IsSquareAttacked(Piece[] squares, int sq, bool byWhite)
    {
        if (byWhite)
        {
            int p1 = sq - 17, p2 = sq - 15;
            if (Board.OnBoard(p1) && squares[p1] == Piece.WPawn) return true;
            if (Board.OnBoard(p2) && squares[p2] == Piece.WPawn) return true;
        }
        else
        {
            int p1 = sq + 17, p2 = sq + 15;
            if (Board.OnBoard(p1) && squares[p1] == Piece.BPawn) return true;
            if (Board.OnBoard(p2) && squares[p2] == Piece.BPawn) return true;
        }

        Piece knight = byWhite ? Piece.WKnight : Piece.BKnight;
        foreach (int d in KnightDeltas)
        {
            int t = sq + d;
            if (Board.OnBoard(t) && squares[t] == knight) return true;
        }

        Piece king = byWhite ? Piece.WKing : Piece.BKing;
        foreach (int d in KingDeltas)
        {
            int t = sq + d;
            if (Board.OnBoard(t) && squares[t] == king) return true;
        }

        Piece bishop = byWhite ? Piece.WBishop : Piece.BBishop;
        Piece rook = byWhite ? Piece.WRook : Piece.BRook;
        Piece queen = byWhite ? Piece.WQueen : Piece.BQueen;

        foreach (int d in BishopDeltas)
        {
            int t = sq + d;
            while (Board.OnBoard(t))
            {
                var pc = squares[t];
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
                var pc = squares[t];
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

    // Allocating wrappers — kept for the many callers that want a fresh list
    // (Perft, San, ChessTactics, PositionContent, MatchRunner, ExtractPv). They
    // delegate to the buffered impls below, so those single sources of truth are
    // exercised by the perft suite that gates correctness.
    public static List<ChessMove> Legal(Board b)
    {
        var pseudo = new List<ChessMove>(64);
        var legal = new List<ChessMove>(48);
        Legal(b, pseudo, legal);
        return legal;
    }

    public static List<ChessMove> Pseudo(Board b)
    {
        var moves = new List<ChessMove>(64);
        Pseudo(b, moves);
        return moves;
    }

    // Buffered: fill caller-provided lists, zero allocation. The Search hot path
    // reuses per-ply buffers here — the engine was allocation-bound at ~484
    // bytes/node (2 lists/node × 77M nodes = ~35GB/bench), profiled via
    // `chess bench` (GH #607). Same moves as the allocating form (perft-gated).
    public static void Legal(Board b, List<ChessMove> pseudoBuf, List<ChessMove> legalBuf)
    {
        Pseudo(b, pseudoBuf);
        legalBuf.Clear();
        bool mover = b.WhiteToMove;
        for (int i = 0; i < pseudoBuf.Count; i++)
        {
            var m = pseudoBuf[i];
            var undo = MoveApply.MakeWithUndo(b, m);
            if (!InCheck(b, mover))
                legalBuf.Add(m);
            MoveApply.Unmake(b, m, undo);
        }
    }

    public static void Pseudo(Board b, List<ChessMove> moves)
    {
        moves.Clear();
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

    /// <summary>
    /// Castling, Chess960 included. The king always ENDS on g/c and the rook on f/d — that
    /// is true of ordinary chess too, which is why one routine covers both. What varies is
    /// where they START, so nothing here may assume e1/a1/h1.
    ///
    /// Three Chess960 shapes have no standard-chess analogue and each is handled below:
    ///   - the king may already stand on its destination, so it moves ZERO squares
    ///   - the rook may stand on the king's destination, or the king on the rook's
    ///   - the path may be blocked by the castling rook itself, which does not block
    /// </summary>
    private static void GenCastle(Board b, int from, bool white, List<ChessMove> moves)
    {
        int rank = white ? 0 : 7;
        if (Board.RankOf(from) != rank) return;
        bool attackerWhite = !white;
        // In check is in check whatever the back rank looks like.
        if (IsSquareAttacked(b, from, attackerWhite)) return;

        var kingRight = white ? CastleRights.WhiteKing : CastleRights.BlackKing;
        var queenRight = white ? CastleRights.WhiteQueen : CastleRights.BlackQueen;

        if ((b.Castle & kingRight) != 0)
            TryCastle(b, from, white, kingSide: true, MoveFlags.CastleKing, moves);
        if ((b.Castle & queenRight) != 0)
            TryCastle(b, from, white, kingSide: false, MoveFlags.CastleQueen, moves);
    }

    private static void TryCastle(
        Board b, int kingFrom, bool white, bool kingSide, MoveFlags flag, List<ChessMove> moves)
    {
        int rank = white ? 0 : 7;
        int rookFrom = Board.Sq(b.CastleRookFile(white, kingSide), rank);
        Piece rook = white ? Piece.WRook : Piece.BRook;
        if (b.Squares[rookFrom] != rook) return;

        int kingTo = Board.Sq(kingSide ? 6 : 2, rank);
        int rookTo = Board.Sq(kingSide ? 5 : 3, rank);

        // Both paths must be clear of everything EXCEPT the two castling pieces, which are
        // allowed to be standing in each other's way — they both move.
        if (!PathClear(b, kingFrom, kingTo, kingFrom, rookFrom)) return;
        if (!PathClear(b, rookFrom, rookTo, kingFrom, rookFrom)) return;

        // The king may not pass through or land on an attacked square. Its own start is
        // already known safe; when kingFrom == kingTo this loop checks that square alone.
        bool attackerWhite = !white;
        int step = kingTo > kingFrom ? 1 : kingTo < kingFrom ? -1 : 0;
        for (int sq = kingFrom; ; sq += step)
        {
            if (IsSquareAttacked(b, sq, attackerWhite)) return;
            if (sq == kingTo || step == 0) break;
        }

        moves.Add(new ChessMove(kingFrom, kingTo, Piece.Empty, flag));
    }

    /// <summary>
    /// Every square strictly between <paramref name="a"/> and <paramref name="b2"/>, plus the
    /// destination, is empty — ignoring the two squares the castling king and rook vacate.
    /// </summary>
    private static bool PathClear(Board b, int a, int b2, int ignore1, int ignore2)
    {
        int step = b2 > a ? 1 : b2 < a ? -1 : 0;
        if (step == 0) return true;
        for (int sq = a + step; ; sq += step)
        {
            if (sq != ignore1 && sq != ignore2 && b.Squares[sq] != Piece.Empty) return false;
            if (sq == b2) break;
        }
        return true;
    }
}
