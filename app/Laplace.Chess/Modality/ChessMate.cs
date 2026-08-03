using System.Numerics;

namespace Laplace.Modality.Chess;

/// <summary>
/// One mate's local geometry, and the key a mate-pattern table would be indexed by.
///
/// Whether a position is checkmate is decided ENTIRELY by the mated king's neighbourhood: its
/// square, which of its <=8 adjacent squares are unavailable and why, and the checking piece's
/// line into it. The rest of the board matters for how the position was reached, never for
/// whether it is mate. That is what makes the pattern space enumerable when the position space
/// is not — legal chess positions are ~10^44 and Syzygy is exhaustive only to 7 men (and stores
/// WDL/DTZ, evaluation, not a mate list), but king square x zone state x checker geometry is
/// small enough to tabulate.
/// </summary>
public readonly record struct ChessMatePattern(
    int KingSquare,
    byte BlockedByOwn,
    byte CoveredByEnemy,
    byte OffBoard,
    ulong Checkers,
    Piece PrimaryChecker,
    bool CheckerAdjacent,
    string? Name)
{
    /// <summary>
    /// Position-independent table key. The king's file/rank and the three zone masks describe
    /// the geometry completely; the checker's type and adjacency finish it. Two mates with the
    /// same key are the same mate pattern regardless of what else is on the board.
    /// </summary>
    public long Key =>
        ((long)KingSquare << 32)
        | ((long)BlockedByOwn << 24)
        | ((long)CoveredByEnemy << 16)
        | ((long)OffBoard << 8)
        | ((long)(byte)(sbyte)PrimaryChecker << 1)
        | (CheckerAdjacent ? 1L : 0L);
}

public static class ChessMate
{
    // Direction order is fixed so the zone masks are comparable across positions:
    // N, NE, E, SE, S, SW, W, NW.
    private static readonly (int df, int dr)[] Zone =
        { (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1) };

    /// <summary>
    /// Checkmate iff the side to move is in check and has no legal move. Both halves are
    /// table-driven now (MoveGen.IsSquareAttacked indexes ChessAttacks; Legal uses pin and
    /// checker masks), so this is a handful of indexed loads rather than a search.
    /// </summary>
    public static bool IsMate(Board b, List<ChessMove> pseudoBuf, List<ChessMove> legalBuf)
    {
        int kingSq = b.FindKing(b.WhiteToMove);
        if (kingSq < 0) return false;
        if (!MoveGen.IsSquareAttacked(b, kingSq, byWhite: !b.WhiteToMove)) return false;
        MoveGen.Legal(b, pseudoBuf, legalBuf);
        return legalBuf.Count == 0;
    }

    public static bool IsMate(Board b) => IsMate(b, new List<ChessMove>(64), new List<ChessMove>(64));

    /// <summary>
    /// Describe the mate geometry around the side-to-move's king, or null when not mate.
    ///
    /// Each of the eight king-zone directions is classified into exactly one of: off the board,
    /// blocked by the king's OWN piece, or covered by the enemy. A square that is empty and safe
    /// cannot exist in a mate — if one did the king would have a move — so the three masks
    /// together always cover all eight directions, which is a useful self-check.
    /// </summary>
    public static ChessMatePattern? Describe(Board b)
    {
        var pseudo = new List<ChessMove>(64);
        var legal = new List<ChessMove>(64);
        if (!IsMate(b, pseudo, legal)) return null;

        bool mover = b.WhiteToMove;
        int kingSq0x88 = b.FindKing(mover);
        int kingBit = (Board.RankOf(kingSq0x88) << 3) | Board.FileOf(kingSq0x88);
        int kf = kingBit & 7, kr = kingBit >> 3;

        // Occupancy WITHOUT the king: a square the king would flee to is still covered if the
        // checking slider's ray passes through where the king currently stands. Removing it is
        // what makes "retreat along the ray" correctly illegal.
        ulong occNoKing = b.OccupiedBB & ~(1UL << kingBit);
        ulong ourOcc = mover ? b.WhiteBB : b.BlackBB;

        byte blocked = 0, covered = 0, off = 0;
        for (int i = 0; i < 8; i++)
        {
            int f = kf + Zone[i].df, r = kr + Zone[i].dr;
            if ((uint)f >= 8 || (uint)r >= 8) { off |= (byte)(1 << i); continue; }
            int t = (r << 3) | f;
            if ((ourOcc & (1UL << t)) != 0) { blocked |= (byte)(1 << i); continue; }
            if (AttackedBy(b, t, !mover, occNoKing)) covered |= (byte)(1 << i);
            // Anything left is an empty, safe square — impossible in a real mate, and the
            // sanity check below asserts it.
        }

        ulong checkers = AttackersTo(b, kingBit, !mover, b.OccupiedBB);
        Piece primary = Piece.Empty;
        bool adjacent = false;
        if (checkers != 0)
        {
            int c = BitOperations.TrailingZeroCount(checkers);
            int c0x88 = Board.Sq(c & 7, c >> 3);
            primary = b.Squares[c0x88];
            adjacent = (ChessAttacks.King(kingBit) & (1UL << c)) != 0;
        }

        var p = new ChessMatePattern(kingBit, blocked, covered, off, checkers, primary, adjacent, null);
        return p with { Name = Classify(p, b, mover) };
    }

    /// <summary>
    /// Name the pattern where the geometry is unambiguous. Deliberately conservative — an
    /// unnamed mate returns null rather than a guess, because a wrong motif label is worse than
    /// no label when it becomes attested evidence.
    /// </summary>
    private static string? Classify(in ChessMatePattern p, Board b, bool mover)
    {
        int kf = p.KingSquare & 7, kr = p.KingSquare >> 3;
        bool onBackRank = mover ? kr == 0 : kr == 7;
        bool inCorner = (kf == 0 || kf == 7) && (kr == 0 || kr == 7);
        Piece checker = Board.TypeOf(p.PrimaryChecker);
        int checkerCount = BitOperations.PopCount(p.Checkers);

        // Smothered: every available neighbour is blocked by the king's OWN pieces, and the
        // check comes from a knight — the only piece that can check through a wall.
        if (checker == Piece.WKnight && p.CoveredByEnemy == 0 && p.BlockedByOwn != 0)
            return "smothered";

        // Back rank: king on its own back rank, the three squares in front blocked by its own
        // pieces, checked along the rank by a rook or queen.
        if (onBackRank && (checker == Piece.WRook || checker == Piece.WQueen) && !p.CheckerAdjacent)
        {
            int fwd = mover ? 1 : -1;
            bool frontBlocked = true;
            for (int df = -1; df <= 1; df++)
            {
                int f = kf + df, r = kr + fwd;
                if ((uint)f >= 8 || (uint)r >= 8) continue;
                if ((( mover ? b.WhiteBB : b.BlackBB) & (1UL << ((r << 3) | f))) == 0) { frontBlocked = false; break; }
            }
            if (frontBlocked) return "back-rank";
        }

        // Epaulette: king on the back rank flanked on BOTH sides by its own pieces, checked
        // frontally — the flanking pieces are the epaulettes that trap it.
        if (onBackRank && kf > 0 && kf < 7)
        {
            ulong own = mover ? b.WhiteBB : b.BlackBB;
            bool left = (own & (1UL << ((kr << 3) | (kf - 1)))) != 0;
            bool right = (own & (1UL << ((kr << 3) | (kf + 1)))) != 0;
            if (left && right && (checker == Piece.WQueen || checker == Piece.WRook))
                return "epaulette";
        }

        // Corner mates: the king is boxed against two edges. Distinguish the knight-assisted
        // Arabian from the heavy-piece box.
        if (inCorner)
        {
            if (checker == Piece.WRook && HasEnemy(b, mover, Piece.WKnight)) return "arabian";
            if (checker == Piece.WQueen || checker == Piece.WRook) return "corner-box";
        }

        if (checkerCount > 1) return "double-check";
        return null;
    }

    private static bool HasEnemy(Board b, bool mover, Piece type)
        => b.PieceBB(mover ? Flip(type) : type) != 0;

    private static Piece Flip(Piece whitePiece) => (Piece)(-(sbyte)whitePiece);

    private static bool AttackedBy(Board b, int sq, bool byWhite, ulong occ)
        => AttackersTo(b, sq, byWhite, occ) != 0;

    private static ulong AttackersTo(Board b, int sq, bool byWhite, ulong occ)
    {
        ulong queens = b.PieceBB(byWhite ? Piece.WQueen : Piece.BQueen);
        return (ChessAttacks.Pawn(sq, !byWhite) & b.PieceBB(byWhite ? Piece.WPawn : Piece.BPawn))
             | (ChessAttacks.Knight(sq) & b.PieceBB(byWhite ? Piece.WKnight : Piece.BKnight))
             | (ChessAttacks.King(sq) & b.PieceBB(byWhite ? Piece.WKing : Piece.BKing))
             | (ChessAttacks.Bishop(sq, occ) & (b.PieceBB(byWhite ? Piece.WBishop : Piece.BBishop) | queens))
             | (ChessAttacks.Rook(sq, occ) & (b.PieceBB(byWhite ? Piece.WRook : Piece.BRook) | queens));
    }
}
