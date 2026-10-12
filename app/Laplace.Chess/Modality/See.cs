namespace Laplace.Modality.Chess;

/// <summary>
/// Static exchange evaluation over the 0x88 board: enumerate the attackers of the move's
/// target square least-valuable-first and swap material off until one side stands pat,
/// negamaxing the speculative gains backward. Pins are ignored (standard SEE); x-rays
/// fall out naturally because each captured/moved piece is removed from the scratch
/// array before the next attacker scan re-walks the rays. Piece values come from
/// <see cref="Search.PieceValue"/> — the engine's one value table (one implementation
/// per fact).
/// </summary>
public static class See
{
    internal static int ValueOf(Piece p)
        => p == Piece.Empty ? 0 : Search.PieceValue[Math.Abs((sbyte)p)];

    /// <summary>
    /// SEE of playing <paramref name="m"/> on <paramref name="b"/>, in centipawns from
    /// the mover's point of view. Negative means the mover comes out of the exchange on
    /// the target square down material against best replies. <paramref name="scratch"/>
    /// (128 squares) lets a per-game loop reuse one buffer across plies.
    /// </summary>
    public static int Evaluate(Board b, ChessMove m, Piece[]? scratch = null)
    {
        var sq = scratch ?? new Piece[128];
        Array.Copy(b.Squares, sq, 128);

        int target = m.To;
        Piece mover = b.MovingPiece(m);
        bool moverWhite = Board.IsWhite(mover);

        Piece firstVictim;
        if ((m.Flags & MoveFlags.EnPassant) != 0)
        {
            int capSq = target + (moverWhite ? -16 : 16);
            firstVictim = sq[capSq];
            sq[capSq] = Piece.Empty;
        }
        else
        {
            firstVictim = sq[target];
        }

        // A promoted pawn fights on in the exchange as its promotion piece.
        Piece occupant = m.IsPromotion ? m.Promotion : mover;
        if (!m.IsDrop) sq[m.From] = Piece.Empty;
        sq[target] = occupant;

        // 2 kings + at most 30 other pieces bounds the swap sequence.
        Span<int> gain = stackalloc int[32];
        int d = 0;
        ulong promoted = b.Promoted;
        bool promotedOccupant = m.IsPromotion || (!m.IsDrop && IsPromoted(m.From));
        gain[0] = CaptureValue(firstVictim, IsPromoted(target));
        bool stm = !moverWhite;

        while (d < gain.Length - 1)
        {
            int from = LeastValuableAttacker(sq, target, stm);
            if (from < 0) break;
            Piece attacker = sq[from];
            // A king may only recapture into a square the opponent no longer defends.
            if (Board.TypeOf(attacker) == Piece.WKing && KingCaptureDefended(sq, target, from, attacker, byWhite: !stm))
                break;
            d++;
            gain[d] = CaptureValue(occupant, promotedOccupant) - gain[d - 1];
            promotedOccupant = IsPromoted(from);
            occupant = attacker;
            sq[from] = Piece.Empty;
            sq[target] = occupant;
            stm = !stm;
        }
        // Negamax the speculative gains backward from the deepest capture: at every
        // depth the defender may stand pat instead of recapturing at a loss.
        for (; d > 0; d--) gain[d - 1] = -Math.Max(-gain[d - 1], gain[d]);
        return gain[0];

        bool IsPromoted(int square) => (promoted & (1UL << Bitboards.Bit(square))) != 0;
        int CaptureValue(Piece piece, bool wasPromoted) => ValueOf(piece)
            + (b.Rules.Drops && piece != Piece.Empty && Board.TypeOf(piece) != Piece.WKing
                ? ValueOf(wasPromoted ? Piece.WPawn : piece) : 0);
    }

    private static bool KingCaptureDefended(Piece[] sq, int target, int from, Piece king, bool byWhite)
    {
        var savedTarget = sq[target];
        sq[from] = Piece.Empty;
        sq[target] = king;
        bool defended = MoveGen.IsSquareAttacked(sq, target, byWhite);
        sq[from] = king;
        sq[target] = savedTarget;
        return defended;
    }

    /// <summary>
    /// Square of the least valuable piece of <paramref name="byWhite"/>'s color attacking
    /// <paramref name="target"/> on the current scratch occupancy, or -1. Scan order is
    /// value order (pawn, knight, bishop, rook, queen, king) so the swap-off always
    /// recaptures with the cheapest unit first.
    /// </summary>
    internal static int LeastValuableAttacker(Piece[] sq, int target, bool byWhite)
    {
        if (byWhite)
        {
            int p1 = target - 17, p2 = target - 15;
            if (Board.OnBoard(p1) && sq[p1] == Piece.WPawn) return p1;
            if (Board.OnBoard(p2) && sq[p2] == Piece.WPawn) return p2;
        }
        else
        {
            int p1 = target + 17, p2 = target + 15;
            if (Board.OnBoard(p1) && sq[p1] == Piece.BPawn) return p1;
            if (Board.OnBoard(p2) && sq[p2] == Piece.BPawn) return p2;
        }

        Piece knight = byWhite ? Piece.WKnight : Piece.BKnight;
        foreach (int d in MoveGen.KnightDeltas)
        {
            int t = target + d;
            if (Board.OnBoard(t) && sq[t] == knight) return t;
        }

        int hit = ScanRays(sq, target, MoveGen.BishopDeltas, byWhite ? Piece.WBishop : Piece.BBishop);
        if (hit >= 0) return hit;

        hit = ScanRays(sq, target, MoveGen.RookDeltas, byWhite ? Piece.WRook : Piece.BRook);
        if (hit >= 0) return hit;

        Piece queen = byWhite ? Piece.WQueen : Piece.BQueen;
        hit = ScanRays(sq, target, MoveGen.BishopDeltas, queen);
        if (hit >= 0) return hit;
        hit = ScanRays(sq, target, MoveGen.RookDeltas, queen);
        if (hit >= 0) return hit;

        Piece king = byWhite ? Piece.WKing : Piece.BKing;
        foreach (int d in MoveGen.KingDeltas)
        {
            int t = target + d;
            if (Board.OnBoard(t) && sq[t] == king) return t;
        }
        return -1;
    }

    private static int ScanRays(Piece[] sq, int target, int[] deltas, Piece want)
    {
        foreach (int d in deltas)
        {
            int t = target + d;
            while (Board.OnBoard(t))
            {
                var pc = sq[t];
                if (pc != Piece.Empty)
                {
                    if (pc == want) return t;
                    break;
                }
                t += d;
            }
        }
        return -1;
    }
}
