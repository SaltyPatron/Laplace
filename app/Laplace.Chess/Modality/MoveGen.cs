using System.Numerics;

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

    /// <summary>
    /// Table-driven. Board maintains its bitboards through Board.Set, so the geometry is an
    /// indexed load per piece class instead of a ray walk per direction. This is the hot path:
    /// Legal() calls it once per pseudo move, ~35 per position, and it measured as the bulk of
    /// replay (46.7% of compose time).
    ///
    /// The mailbox overload below is retained and still correct — See needs it to probe
    /// hypothetical occupancies over a raw array with no Board to update. The two are pinned
    /// equivalent by MoveGenBitboardEquivalenceTests.
    /// </summary>
    public static bool IsSquareAttacked(Board b, int sq0x88, bool byWhite)
    {
        int sq = (Board.RankOf(sq0x88) << 3) | Board.FileOf(sq0x88);
        ulong occ = b.OccupiedBB;

        // A white pawn attacks sq iff a black pawn ON sq would attack that pawn's square.
        if ((ChessAttacks.Pawn(sq, !byWhite) & b.PieceBB(byWhite ? Piece.WPawn : Piece.BPawn)) != 0) return true;
        if ((ChessAttacks.Knight(sq) & b.PieceBB(byWhite ? Piece.WKnight : Piece.BKnight)) != 0) return true;
        if ((ChessAttacks.King(sq) & b.PieceBB(byWhite ? Piece.WKing : Piece.BKing)) != 0) return true;

        ulong queens = b.PieceBB(byWhite ? Piece.WQueen : Piece.BQueen);
        if ((ChessAttacks.Bishop(sq, occ) & (b.PieceBB(byWhite ? Piece.WBishop : Piece.BBishop) | queens)) != 0)
            return true;
        if ((ChessAttacks.Rook(sq, occ) & (b.PieceBB(byWhite ? Piece.WRook : Piece.BRook) | queens)) != 0)
            return true;

        return false;
    }

    /// <summary>
    /// Table-driven form: no ray walking, no board scan. Attacks are symmetric, so "is square X
    /// attacked by a rook/queen" is "does a rook placed on X see one" — one indexed load per
    /// piece class instead of a loop per direction.
    ///
    /// Takes a Bitboards VALUE rather than a Board, for callers that already hold one and have
    /// no Board to hand — PositionContent.Surface builds one every ply. Board itself now
    /// maintains bitboards incrementally through Board.Set, so the Board overload of
    /// IsSquareAttacked above is the one to reach for in the hot path; prefer it unless you
    /// genuinely only have a Bitboards.
    ///
    /// ChessAttacksTests pins the tables; MoveGenBitboardEquivalenceTests pins this against the
    /// mailbox walk on real positions and through real play.
    ///
    /// <paramref name="sq"/> is a 0-63 BIT index (Bitboards.Bit), not a 0x88 square.
    /// </summary>
    public static bool IsSquareAttackedBB(in Bitboards bb, int sq, bool byWhite)
    {
        ulong occ = bb.Occupied;

        // Pawns: a white pawn attacks sq iff a BLACK pawn on sq would attack that pawn's square.
        ulong pawns = byWhite ? bb.Of(Piece.WPawn) : bb.Of(Piece.BPawn);
        if ((ChessAttacks.Pawn(sq, !byWhite) & pawns) != 0) return true;

        if ((ChessAttacks.Knight(sq) & (byWhite ? bb.Of(Piece.WKnight) : bb.Of(Piece.BKnight))) != 0) return true;
        if ((ChessAttacks.King(sq) & (byWhite ? bb.Of(Piece.WKing) : bb.Of(Piece.BKing))) != 0) return true;

        ulong queens = byWhite ? bb.Of(Piece.WQueen) : bb.Of(Piece.BQueen);
        ulong bishops = (byWhite ? bb.Of(Piece.WBishop) : bb.Of(Piece.BBishop)) | queens;
        if ((ChessAttacks.Bishop(sq, occ) & bishops) != 0) return true;

        ulong rooks = (byWhite ? bb.Of(Piece.WRook) : bb.Of(Piece.BRook)) | queens;
        if ((ChessAttacks.Rook(sq, occ) & rooks) != 0) return true;

        return false;
    }

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

        // The mover's king is in ONE place for the whole pseudo list, so locate it once instead
        // of once per candidate. InCheck -> FindKing was a linear 64-square scan per pseudo move:
        // ~35 moves x ~70 plies = ~2,450 scans per game, ~157k square reads, to find a piece that
        // moves at most once per ply. Rescan only when this move actually relocates the king.
        //
        // Castling MUST rescan rather than trusting m.To: the Chess960 path is atomic and the
        // king can castle WITHOUT MOVING (king g1, rook h1 => To == From), and its destination
        // can hold the castling rook, so m.To is not the king's resting square in general
        // (MoveApply.MakeWithUndo, the isCastle branch). Castles are at most twice a game, so
        // the fallback costs nothing measurable.
        int kingSq = b.FindKing(mover);
        const MoveFlags CastleAny = MoveFlags.Castle;

        if (kingSq < 0)
        {
            // No king on the board (constructed test positions, some puzzles). Nothing can be
            // pinned or checked, so every pseudo move is legal and the mask path is undefined.
            for (int i = 0; i < pseudoBuf.Count; i++) legalBuf.Add(pseudoBuf[i]);
            return;
        }

        // ONE checker/pin computation per position, replacing ~35 make/unmake round trips.
        //
        // A non-king move is legal iff it neither leaves an existing check standing nor opens a
        // new line to the king. Both are decidable from two masks computed once:
        //   checkers  enemy pieces attacking the king right now
        //   pinned    own pieces that are the SOLE occupant between the king and an enemy slider
        //
        // King moves, castles and en passant keep the make/unmake path deliberately. The king
        // moving changes the attack set it is being tested against (it can retreat along the
        // checking ray and still be attacked); Chess960 castling can leave the king on its own
        // square or land it on the rook's; and en passant removes a pawn from a DIFFERENT square
        // than the destination, which can open a rank onto the king. Each is rare — at most a
        // handful per position — and each is a classic source of silent move-generation bugs, so
        // they stay on the path perft has always validated.
        int kingBit = (Board.RankOf(kingSq) << 3) | Board.FileOf(kingSq);
        ulong occ = b.OccupiedBB;
        ulong ourOcc = mover ? b.WhiteBB : b.BlackBB;

        ulong checkers = AttackersTo(b, kingBit, byWhite: !mover, occ);
        int checkCount = BitOperations.PopCount(checkers);
        ulong pinned = PinnedTo(b, kingBit, mover, occ, ourOcc);

        // Under single check a non-king move must capture the checker or interpose on its ray.
        ulong checkEvasion = ulong.MaxValue;
        if (checkCount == 1)
        {
            int checkerBit = BitOperations.TrailingZeroCount(checkers);
            checkEvasion = checkers | ChessAttacks.Between(kingBit, checkerBit);
        }

        for (int i = 0; i < pseudoBuf.Count; i++)
        {
            var m = pseudoBuf[i];
            bool special = m.From == kingSq
                        || (m.Flags & CastleAny) != 0
                        || (m.Flags & MoveFlags.EnPassant) != 0;

            if (special)
            {
                var undo = MoveApply.MakeWithUndo(b, m);
                int k = (m.From == kingSq || (m.Flags & CastleAny) != 0) ? b.FindKing(mover) : kingSq;
                if (k < 0 || !IsSquareAttacked(b, k, byWhite: !mover)) legalBuf.Add(m);
                MoveApply.Unmake(b, m, undo);
                continue;
            }

            if (checkCount > 1) continue;   // double check: only the king may move

            int fromBit = (Board.RankOf(m.From) << 3) | Board.FileOf(m.From);
            int toBit = (Board.RankOf(m.To) << 3) | Board.FileOf(m.To);

            if ((checkEvasion & (1UL << toBit)) == 0) continue;

            // A pinned piece may only move along the line it is pinned on — which includes
            // capturing the pinner and retreating toward the king.
            if ((pinned & (1UL << fromBit)) != 0
                && (ChessAttacks.Line(kingBit, fromBit) & (1UL << toBit)) == 0) continue;

            legalBuf.Add(m);
        }
    }

    /// <summary>Every piece of <paramref name="byWhite"/> attacking <paramref name="sq"/> (bit index).</summary>
    private static ulong AttackersTo(Board b, int sq, bool byWhite, ulong occ)
    {
        ulong queens = b.PieceBB(byWhite ? Piece.WQueen : Piece.BQueen);
        return (ChessAttacks.Pawn(sq, !byWhite) & b.PieceBB(byWhite ? Piece.WPawn : Piece.BPawn))
             | (ChessAttacks.Knight(sq) & b.PieceBB(byWhite ? Piece.WKnight : Piece.BKnight))
             | (ChessAttacks.King(sq) & b.PieceBB(byWhite ? Piece.WKing : Piece.BKing))
             | (ChessAttacks.Bishop(sq, occ) & (b.PieceBB(byWhite ? Piece.WBishop : Piece.BBishop) | queens))
             | (ChessAttacks.Rook(sq, occ) & (b.PieceBB(byWhite ? Piece.WRook : Piece.BRook) | queens));
    }

    /// <summary>
    /// Own pieces that are the SOLE occupant between the king and an enemy slider aligned with
    /// it. Snipers are found on an EMPTY board (occ = 0) so blockers do not hide them, then each
    /// ray is re-checked against the real occupancy.
    /// </summary>
    private static ulong PinnedTo(Board b, int kingBit, bool mover, ulong occ, ulong ourOcc)
    {
        ulong queens = b.PieceBB(mover ? Piece.BQueen : Piece.WQueen);
        ulong theirRooks = b.PieceBB(mover ? Piece.BRook : Piece.WRook) | queens;
        ulong theirBishops = b.PieceBB(mover ? Piece.BBishop : Piece.WBishop) | queens;

        ulong snipers = (ChessAttacks.Rook(kingBit, 0) & theirRooks)
                      | (ChessAttacks.Bishop(kingBit, 0) & theirBishops);

        ulong pinned = 0;
        while (snipers != 0)
        {
            int s = BitOperations.TrailingZeroCount(snipers);
            snipers &= snipers - 1;
            ulong between = ChessAttacks.Between(kingBit, s) & occ;
            if (between != 0 && (between & (between - 1)) == 0 && (between & ourOcc) != 0)
                pinned |= between;
        }
        return pinned;
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
            GenFrom(b, sq, p, white, moves);
        }
    }

    /// <summary>Pseudo-legal moves from one occupied square (PGN SAN resolve hot path).</summary>
    public static void PseudoFrom(Board b, int from, List<ChessMove> moves)
    {
        moves.Clear();
        var p = b.Squares[from];
        if (p == Piece.Empty) return;
        bool white = Board.IsWhite(p);
        if (white != b.WhiteToMove) return;
        GenFrom(b, from, p, white, moves);
    }

    private static void GenFrom(Board b, int sq, Piece p, bool white, List<ChessMove> moves)
    {
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

    /// <summary>
    /// Single-move legality for SAN-directed resolve. Uses the same pin/check masks as
    /// <see cref="Legal"/> for ordinary moves; make/unmake only for king/castle/EP.
    /// </summary>
    public static bool IsLegal(Board b, ChessMove m)
    {
        bool mover = b.WhiteToMove;
        int kingSq = b.FindKing(mover);
        const MoveFlags CastleAny = MoveFlags.Castle;
        if (kingSq < 0) return true;

        bool special = m.From == kingSq
                    || (m.Flags & CastleAny) != 0
                    || (m.Flags & MoveFlags.EnPassant) != 0;
        if (special)
        {
            var undo = MoveApply.MakeWithUndo(b, m);
            int k = (m.From == kingSq || (m.Flags & CastleAny) != 0) ? b.FindKing(mover) : kingSq;
            bool ok = k >= 0 && !IsSquareAttacked(b, k, byWhite: !mover);
            MoveApply.Unmake(b, m, undo);
            return ok;
        }

        int kingBit = (Board.RankOf(kingSq) << 3) | Board.FileOf(kingSq);
        ulong occ = b.OccupiedBB;
        ulong ourOcc = mover ? b.WhiteBB : b.BlackBB;
        ulong checkers = AttackersTo(b, kingBit, byWhite: !mover, occ);
        int checkCount = BitOperations.PopCount(checkers);
        if (checkCount > 1) return false;

        ulong checkEvasion = ulong.MaxValue;
        if (checkCount == 1)
        {
            int checkerBit = BitOperations.TrailingZeroCount(checkers);
            checkEvasion = checkers | ChessAttacks.Between(kingBit, checkerBit);
        }

        int fromBit = (Board.RankOf(m.From) << 3) | Board.FileOf(m.From);
        int toBit = (Board.RankOf(m.To) << 3) | Board.FileOf(m.To);
        if ((checkEvasion & (1UL << toBit)) == 0) return false;

        ulong pinned = PinnedTo(b, kingBit, mover, occ, ourOcc);
        if ((pinned & (1UL << fromBit)) != 0
            && (ChessAttacks.Line(kingBit, fromBit) & (1UL << toBit)) == 0)
            return false;
        return true;
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
            TryCastle(b, from, white, kingSide: true, moves);
        if ((b.Castle & queenRight) != 0)
            TryCastle(b, from, white, kingSide: false, moves);
    }

    private static void TryCastle(
        Board b, int kingFrom, bool white, bool kingSide, List<ChessMove> moves)
    {
        int rank = white ? 0 : 7;
        int rookFrom = Board.Sq(b.CastleRookFile(white, kingSide), rank);
        Piece rook = white ? Piece.WRook : Piece.BRook;
        if (b.Squares[rookFrom] != rook) return;

        int kingFile = Board.FileOf(kingFrom);
        int rookFile = Board.FileOf(rookFrom);
        int kingTo = Board.Sq(kingSide ? CastlePaths.KingSideKingFile : CastlePaths.QueenSideKingFile, rank);

        // Emptiness in ONE AND. Which squares must be clear is a pure function of where the
        // king and rook start — destinations are fixed — so it is a precomputed file mask
        // rather than two walks per generated move. The castling pair's own squares are
        // already excluded from the mask, because they both move and cannot block each other.
        if ((CastlePaths.OccupiedFiles(b, rank) & CastlePaths.EmptyMask(kingFile, rookFile)) != 0)
            return;

        // The king may not start in, pass through, or land on check. The square set is
        // precomputed too; only the attack test itself is per-square, because that depends
        // on the whole position rather than on the geometry.
        bool attackerWhite = !white;
        for (byte path = CastlePaths.KingPathMask(kingFile, rookFile); path != 0; path &= (byte)(path - 1))
        {
            int f = System.Numerics.BitOperations.TrailingZeroCount(path);
            if (IsSquareAttacked(b, Board.Sq(f, rank), attackerWhite)) return;
        }

        moves.Add(new ChessMove(kingFrom, kingTo, Piece.Empty, MoveFlags.Castle));
    }

}
