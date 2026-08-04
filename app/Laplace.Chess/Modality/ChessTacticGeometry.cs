using System.Numerics;

namespace Laplace.Modality.Chess;

/// <summary>
/// A tactical motif reduced to the part that RECURS, and nothing else.
///
/// The position a fork occurs in is almost always unique — MEASURED: 92.0% of MOVE consensus
/// cells have witness_count = 1. Conditioning an outcome query on the position is therefore
/// conditioning on a sample of one. But the MOTIF is drawn from a tiny closed alphabet:
/// "knight forks king and rook" is the same claim in every game it ever appears in, so its cell
/// accumulates thousands of witnesses while the positions underneath it each appear once.
///
///   (this exact position, OUTCOME, win)     witness_count = 1        meaningless
///   (knight-fork-king-rook, OUTCOME, win)   witness_count = 10,000s  a real statistic
///
/// That is what makes single-witness positions disposable rather than merely expensive: their
/// content is the motifs they exhibit plus the movetext for exact replay.
///
/// Detection is two instructions over the attack tables, so this costs essentially nothing at
/// ingest — a fork is popcount(attacks &amp; valuable_enemies) >= 2, a pin is
/// between(king, slider) &amp; occupancy holding exactly one piece.
/// </summary>
public readonly record struct ChessTacticPattern(
    TacticKind Kind,
    Piece Attacker,
    Piece Victim1,
    Piece Victim2)
{
    /// <summary>
    /// Position-independent cell subject. Piece types are colour-normalised so a white knight
    /// forking a black king+rook and the mirror image are ONE claim, and victims are ordered by
    /// value so (king, rook) and (rook, king) do not split the witnesses in half — the same
    /// canonical-ordering rule identity claims need.
    /// </summary>
    public long Key =>
        ((long)Kind << 24)
        | ((long)Math.Abs((sbyte)Attacker) << 16)
        | ((long)Math.Abs((sbyte)Victim1) << 8)
        | (long)Math.Abs((sbyte)Victim2);

    public override string ToString() =>
        $"{Kind.ToString().ToLowerInvariant()}:{Name(Attacker)}>{Name(Victim1)}+{Name(Victim2)}";

    private static string Name(Piece p) => Board.TypeOf(p) switch
    {
        Piece.WPawn => "P", Piece.WKnight => "N", Piece.WBishop => "B",
        Piece.WRook => "R", Piece.WQueen => "Q", Piece.WKing => "K", _ => "-",
    };
}

public enum TacticKind
{
    Fork = 1,
    Pin = 2,
    Skewer = 3,
}

public static class ChessTacticGeometry
{
    // Only used to order victims canonically and to decide what is worth forking. Not an
    // evaluation — the fold supplies value; this is just a stable ordering.
    private static int Value(Piece p) => Board.TypeOf(p) switch
    {
        Piece.WPawn => 1, Piece.WKnight => 3, Piece.WBishop => 3,
        Piece.WRook => 5, Piece.WQueen => 9, Piece.WKing => 100, _ => 0,
    };

    /// <summary>
    /// Every fork <paramref name="byWhite"/> currently has: one piece attacking two or more
    /// enemy pieces at least as valuable as itself (or the king, which is always worth it).
    /// The attacker-value test is what separates a fork from an ordinary double attack on
    /// pawns, which is not a motif anyone plays for.
    /// </summary>
    public static List<ChessTacticPattern> Forks(Board b, bool byWhite)
    {
        var found = new List<ChessTacticPattern>();
        ulong occ = b.OccupiedBB;
        ulong enemy = byWhite ? b.BlackBB : b.WhiteBB;

        for (int sq = 0; sq < 64; sq++)
        {
            int sq0x88 = Board.Sq(sq & 7, sq >> 3);
            var piece = b.Squares[sq0x88];
            if (piece == Piece.Empty || Board.IsWhite(piece) != byWhite) continue;

            ulong att = AttacksOf(piece, sq, occ, byWhite) & enemy;
            if (BitOperations.PopCount(att) < 2) continue;

            // Victims worth forking: at least as valuable as the forker, or the king.
            int av = Value(piece);
            var victims = new List<Piece>(4);
            ulong scan = att;
            while (scan != 0)
            {
                int v = BitOperations.TrailingZeroCount(scan);
                scan &= scan - 1;
                var vp = b.Squares[Board.Sq(v & 7, v >> 3)];
                if (Value(vp) >= av || Board.TypeOf(vp) == Piece.WKing) victims.Add(vp);
            }
            if (victims.Count < 2) continue;

            victims.Sort((x, y) => Value(y).CompareTo(Value(x)));   // canonical: richest first
            found.Add(new ChessTacticPattern(TacticKind.Fork, piece, victims[0], victims[1]));
        }
        return found;
    }

    /// <summary>
    /// Pins and skewers share one geometry — slider, single blocker, target behind — and differ
    /// only in which end is worth more. Front more valuable than back is a SKEWER (the valuable
    /// piece must move and exposes the lesser); back more valuable is a PIN (the lesser piece
    /// cannot move without exposing the greater).
    /// </summary>
    public static List<ChessTacticPattern> PinsAndSkewers(Board b, bool byWhite)
    {
        var found = new List<ChessTacticPattern>();
        ulong occ = b.OccupiedBB;
        ulong victimSide = byWhite ? b.BlackBB : b.WhiteBB;

        ulong queens = b.PieceBB(byWhite ? Piece.WQueen : Piece.BQueen);
        ulong rooks = b.PieceBB(byWhite ? Piece.WRook : Piece.BRook) | queens;
        ulong bishops = b.PieceBB(byWhite ? Piece.WBishop : Piece.BBishop) | queens;

        for (int s = 0; s < 64; s++)
        {
            bool isRookLine = (rooks & (1UL << s)) != 0;
            bool isBishopLine = (bishops & (1UL << s)) != 0;
            if (!isRookLine && !isBishopLine) continue;
            var slider = b.Squares[Board.Sq(s & 7, s >> 3)];

            // Targets this slider would reach on an EMPTY board — blockers must not hide them.
            ulong reach = (isRookLine ? ChessAttacks.Rook(s, 0) : 0UL)
                        | (isBishopLine ? ChessAttacks.Bishop(s, 0) : 0UL);
            ulong targets = reach & victimSide;

            while (targets != 0)
            {
                int t = BitOperations.TrailingZeroCount(targets);
                targets &= targets - 1;

                ulong between = ChessAttacks.Between(s, t) & occ;
                if (between == 0 || (between & (between - 1)) != 0) continue;   // need exactly one
                if ((between & victimSide) == 0) continue;                      // blocker must be theirs

                int f = BitOperations.TrailingZeroCount(between);
                var front = b.Squares[Board.Sq(f & 7, f >> 3)];
                var back = b.Squares[Board.Sq(t & 7, t >> 3)];

                var kind = Value(front) > Value(back) ? TacticKind.Skewer : TacticKind.Pin;
                found.Add(new ChessTacticPattern(kind, slider, front, back));
            }
        }
        return found;
    }

    private static ulong AttacksOf(Piece piece, int sq, ulong occ, bool white) =>
        Board.TypeOf(piece) switch
        {
            Piece.WPawn => ChessAttacks.Pawn(sq, white),
            Piece.WKnight => ChessAttacks.Knight(sq),
            Piece.WBishop => ChessAttacks.Bishop(sq, occ),
            Piece.WRook => ChessAttacks.Rook(sq, occ),
            Piece.WQueen => ChessAttacks.Queen(sq, occ),
            Piece.WKing => ChessAttacks.King(sq),
            _ => 0UL,
        };
}
