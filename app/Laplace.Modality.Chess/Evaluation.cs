using System.Numerics;

namespace Laplace.Modality.Chess;

/// <summary>
/// Independently toggleable evaluation overlays. Each term is colour-symmetric, so ANY subset preserves
/// the mirror invariant — letting you ablate a term (measure its Elo contribution) or, later, swap a
/// hand-tuned overlay for a substrate-LEARNED one (e.g. piece-square values measured from the 29M-move
/// corpus — the same 768 piece-square atoms <see cref="ChessCompose"/>-style composition already uses)
/// without disturbing the rest.
/// </summary>
[Flags]
public enum EvalTerm
{
    None          = 0,
    Material      = 1 << 0,  // tapered material values
    Pst           = 1 << 1,  // tapered piece-square tables (the PeSTO prior; swappable for a learned overlay)
    BishopPair    = 1 << 2,
    RookFiles     = 1 << 3,  // rooks on open / semi-open files
    PawnStructure = 1 << 4,  // doubled + isolated penalties
    Tempo         = 1 << 5,
    All           = Material | Pst | BishopPair | RookFiles | PawnStructure | Tempo,
}

/// <summary>
/// A classical, tapered (mid→endgame) static evaluation in centipawns — the search's leaf judgment.
/// Pure C#, no DB/native, deterministic. Built from the public-domain <b>PeSTO</b> material + piece-square
/// tables (Rofchade), plus a handful of cheap structural terms (bishop pair, rooks on (semi-)open files,
/// doubled/isolated pawns, tempo). The score is returned <b>relative to the side to move</b> (positive =
/// the mover is better), the negamax convention the search expects.
///
/// <para>Correctness is gated by a mirror-symmetry invariant (see the tests): a position and its
/// colour-swapped vertical mirror must evaluate identically, which catches every board-orientation and
/// sign bug. The substrate's learned positional value blends in ABOVE this, in the search root — this
/// term is the fast, exact, hand-checkable floor.</para>
/// </summary>
public static class Evaluation
{
    // Game-phase weights per piece type (P,N,B,R,Q,K) → 24 at the full opening, decreasing as pieces
    // come off. The tapered score interpolates the midgame and endgame evaluations by this phase.
    private static readonly int[] PhaseInc = { 0, 1, 1, 2, 4, 0 };
    private const int PhaseTotal = 24;

    // Material (centipawns), midgame and endgame, indexed by piece type 1..6 → 0..5.
    private static readonly int[] MgMaterial = { 82, 337, 365, 477, 1025, 0 };
    private static readonly int[] EgMaterial = { 94, 281, 297, 512, 936, 0 };

    /// <summary>Evaluate <paramref name="b"/> in centipawns from the side-to-move's perspective, using
    /// only the enabled overlay <paramref name="terms"/> (default: all). <paramref name="mgPstOverride"/>/
    /// <paramref name="egPstOverride"/> swap the hand-tuned PeSTO piece-square tables for substrate-LEARNED
    /// ones (same <c>[type-1][idx]</c> shape); null keeps PeSTO — so default behaviour is unchanged.</summary>
    public static int Evaluate(
        Board b, EvalTerm terms = EvalTerm.All, int[][]? mgPstOverride = null, int[][]? egPstOverride = null)
    {
        var mgTab = mgPstOverride ?? MgPst;
        var egTab = egPstOverride ?? EgPst;
        int mgMat = 0, egMat = 0, mgPst = 0, egPst = 0, phase = 0;
        int wBishops = 0, bBishops = 0;
        ulong wPawns = 0, bPawns = 0;

        // Single board scan: accumulate White-positive material and PST separately (so either can be
        // toggled), the game phase, and the bits the structural terms need.
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = b.Squares[sq];
            if (p == Piece.Empty) continue;

            int type = Math.Abs((sbyte)p);     // 1..6
            int ti = type - 1;
            bool white = (sbyte)p > 0;
            int file = Board.FileOf(sq), rank = Board.RankOf(sq);
            int idx = white ? (7 - rank) * 8 + file : rank * 8 + file; // PST written rank-8-first, White POV
            int sign = white ? 1 : -1;

            mgMat += sign * MgMaterial[ti];
            egMat += sign * EgMaterial[ti];
            mgPst += sign * mgTab[ti][idx];
            egPst += sign * egTab[ti][idx];
            phase += PhaseInc[ti];

            switch (type)
            {
                case 1: if (white) wPawns |= 1UL << Bb(sq); else bPawns |= 1UL << Bb(sq); break;
                case 3: if (white) wBishops++; else bBishops++; break;
            }
        }

        // Tapered group (material + PST): sum the enabled mg/eg contributions, then taper ONCE — so any
        // subset is exact (no per-term integer-division rounding) and stays mirror-symmetric.
        int mg = 0, eg = 0;
        if ((terms & EvalTerm.Material) != 0) { mg += mgMat; eg += egMat; }
        if ((terms & EvalTerm.Pst) != 0)      { mg += mgPst; eg += egPst; }
        int white_cp = TaperedToWhite(mg, eg, phase);

        if ((terms & EvalTerm.BishopPair) != 0)
        {
            if (wBishops >= 2) white_cp += BishopPair;
            if (bBishops >= 2) white_cp -= BishopPair;
        }
        if ((terms & EvalTerm.RookFiles) != 0)
        {
            white_cp += RookFileTerm(b, wPawns, bPawns, white: true);
            white_cp -= RookFileTerm(b, bPawns, wPawns, white: false);
        }
        if ((terms & EvalTerm.PawnStructure) != 0)
        {
            white_cp -= DoubledIsolatedPenalty * (Bitboards.Doubled(wPawns) + Bitboards.Isolated(wPawns));
            white_cp += DoubledIsolatedPenalty * (Bitboards.Doubled(bPawns) + Bitboards.Isolated(bPawns));
        }

        // Flip to the side-to-move's perspective, then add the tempo (so it always favours the mover).
        int stm = b.WhiteToMove ? white_cp : -white_cp;
        if ((terms & EvalTerm.Tempo) != 0) stm += Tempo;
        return stm;
    }

    private const int BishopPair             = 30;
    private const int RookOpenFile           = 25;
    private const int RookSemiOpenFile       = 12;
    private const int DoubledIsolatedPenalty = 8;
    private const int Tempo                  = 10;

    private static int TaperedToWhite(int mg, int eg, int phase)
    {
        int p = Math.Min(phase, PhaseTotal);
        return (mg * p + eg * (PhaseTotal - p)) / PhaseTotal;
    }

    /// <summary>Bonus for each rook of <paramref name="white"/> on an open (no pawns) or semi-open
    /// (no friendly pawns) file.</summary>
    private static int RookFileTerm(Board b, ulong ownPawns, ulong enemyPawns, bool white)
    {
        Piece rook = white ? Piece.WRook : Piece.BRook;
        int bonus = 0;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            if (b.Squares[sq] != rook) continue;
            ulong fileMask = Bitboards.FileMask(Board.FileOf(sq));
            if ((ownPawns & fileMask) != 0) continue;          // own pawn on the file → not (semi-)open
            bonus += (enemyPawns & fileMask) == 0 ? RookOpenFile : RookSemiOpenFile;
        }
        return bonus;
    }

    // 0x88 square → little-endian rank-file bit (a1=0), matching Bitboards.
    private static int Bb(int sq0x88) => (Board.RankOf(sq0x88) << 3) | Board.FileOf(sq0x88);

    // ---- PeSTO piece-square tables (centipawns), written rank 8 (top) → rank 1, file a→h, White POV.
    //      Indexed [pieceType-1][ (7-rank)*8 + file ] for White; [type-1][ rank*8 + file ] for Black. ----

    private static readonly int[] MgPawn = {
          0,   0,   0,   0,   0,   0,   0,   0,
         98, 134,  61,  95,  68, 126,  34, -11,
         -6,   7,  26,  31,  65,  56,  25, -20,
        -14,  13,   6,  21,  23,  12,  17, -23,
        -27,  -2,  -5,  12,  17,   6,  10, -25,
        -26,  -4,  -4, -10,   3,   3,  33, -12,
        -35,  -1, -20, -23, -15,  24,  38, -22,
          0,   0,   0,   0,   0,   0,   0,   0,
    };
    private static readonly int[] EgPawn = {
          0,   0,   0,   0,   0,   0,   0,   0,
        178, 173, 158, 134, 147, 132, 165, 187,
         94, 100,  85,  67,  56,  53,  82,  84,
         32,  24,  13,   5,  -2,   4,  17,  17,
         13,   9,  -3,  -7,  -7,  -8,   3,  -1,
          4,   7,  -6,   1,   0,  -5,  -1,  -8,
         13,   8,   8,  10,  13,   0,   2,  -7,
          0,   0,   0,   0,   0,   0,   0,   0,
    };
    private static readonly int[] MgKnight = {
       -167, -89, -34, -49,  61, -97, -15,-107,
        -73, -41,  72,  36,  23,  62,   7, -17,
        -47,  60,  37,  65,  84, 129,  73,  44,
         -9,  17,  19,  53,  37,  69,  18,  22,
        -13,   4,  16,  13,  28,  19,  21,  -8,
        -23,  -9,  12,  10,  19,  17,  25, -16,
        -29, -53, -12,  -3,  -1,  18, -14, -19,
       -105, -21, -58, -33, -17, -28, -19, -23,
    };
    private static readonly int[] EgKnight = {
        -58, -38, -13, -28, -31, -27, -63, -99,
        -25,  -8, -25,  -2,  -9, -25, -24, -52,
        -24, -20,  10,   9,  -1,  -9, -19, -41,
        -17,   3,  22,  22,  22,  11,   8, -18,
        -18,  -6,  16,  25,  16,  17,   4, -18,
        -23,  -3,  -1,  15,  10,  -3, -20, -22,
        -42, -20, -10,  -5,  -2, -20, -23, -44,
        -29, -51, -23, -15, -22, -18, -50, -64,
    };
    private static readonly int[] MgBishop = {
        -29,   4, -82, -37, -25, -42,   7,  -8,
        -26,  16, -18, -13,  30,  59,  18, -47,
        -16,  37,  43,  40,  35,  50,  37,  -2,
         -4,   5,  19,  50,  37,  37,   7,  -2,
         -6,  13,  13,  26,  34,  12,  10,   4,
          0,  15,  15,  15,  14,  27,  18,  10,
          4,  15,  16,   0,   7,  21,  33,   1,
        -33,  -3, -14, -21, -13, -12, -39, -21,
    };
    private static readonly int[] EgBishop = {
        -14, -21, -11,  -8,  -7,  -9, -17, -24,
         -8,  -4,   7, -12,  -3, -13,  -4, -14,
          2,  -8,   0,  -1,  -2,   6,   0,   4,
         -3,   9,  12,   9,  14,  10,   3,   2,
         -6,   3,  13,  19,   7,  10,  -3,  -9,
        -12,  -3,   8,  10,  13,   3,  -7, -15,
        -14, -18,  -7,  -1,   4,  -9, -15, -27,
        -23,  -9, -23,  -5,  -9, -16,  -5, -17,
    };
    private static readonly int[] MgRook = {
         32,  42,  32,  51,  63,   9,  31,  43,
         27,  32,  58,  62,  80,  67,  26,  44,
         -5,  19,  26,  36,  17,  45,  61,  16,
        -24, -11,   7,  26,  24,  35,  -8, -20,
        -36, -26, -12,  -1,   9,  -7,   6, -23,
        -45, -25, -16, -17,   3,   0,  -5, -33,
        -44, -16, -20,  -9,  -1,  11,  -6, -71,
        -19, -13,   1,  17,  16,   7, -37, -26,
    };
    private static readonly int[] EgRook = {
         13,  10,  18,  15,  12,  12,   8,   5,
         11,  13,  13,  11,  -3,   3,   8,   3,
          7,   7,   7,   5,   4,  -3,  -5,  -3,
          4,   3,  13,   1,   2,   1,  -1,   2,
          3,   5,   8,   4,  -5,  -6,  -8, -11,
         -4,   0,  -5,  -1,  -7, -12,  -8, -16,
         -6,  -6,   0,   2,  -9,  -9, -11,  -3,
         -9,   2,   3,  -1,  -5, -13,   4, -20,
    };
    private static readonly int[] MgQueen = {
        -28,   0,  29,  12,  59,  44,  43,  45,
        -24, -39,  -5,   1, -16,  57,  28,  54,
        -13, -17,   7,   8,  29,  56,  47,  57,
        -27, -27, -16, -16,  -1,  17,  -2,   1,
         -9, -26,  -9, -10,  -2,  -4,   3,  -3,
        -14,   2, -11,  -2,  -5,   2,  14,   5,
        -35,  -8,  11,   2,   8,  15,  -3,   1,
         -1, -18,  -9,  10, -15, -25, -31, -50,
    };
    private static readonly int[] EgQueen = {
         -9,  22,  22,  27,  27,  19,  10,  20,
        -17,  20,  32,  41,  58,  25,  30,   0,
        -20,   6,   9,  49,  47,  35,  19,   9,
          3,  22,  24,  45,  57,  40,  57,  36,
        -18,  28,  19,  47,  31,  34,  39,  23,
        -16, -27,  15,   6,   9,  17,  10,   5,
        -22, -23, -30, -16, -16, -23, -36, -32,
        -33, -28, -22, -43,  -5, -32, -20, -41,
    };
    private static readonly int[] MgKing = {
        -65,  23,  16, -15, -56, -34,   2,  13,
         29,  -1, -20,  -7,  -8,  -4, -38, -29,
         -9,  24,   2, -16, -20,   6,  22, -22,
        -17, -20, -12, -27, -30, -25, -14, -36,
        -49,  -1, -27, -39, -46, -44, -33, -51,
        -14, -14, -22, -46, -44, -30, -15, -27,
          1,   7,  -8, -64, -43, -16,   9,   8,
        -15,  36,  12, -54,   8, -28,  24,  14,
    };
    private static readonly int[] EgKing = {
        -74, -35, -18, -18, -11,  15,   4, -17,
        -12,  17,  14,  17,  17,  38,  23,  11,
         10,  17,  23,  15,  20,  45,  44,  13,
         -8,  22,  24,  27,  26,  33,  26,   3,
        -18,  -4,  21,  24,  27,  23,   9, -11,
        -19,  -3,  11,  21,  23,  16,   7,  -9,
        -27, -11,   4,  13,  14,   4,  -5, -17,
        -53, -34, -21, -11, -28, -14, -24, -43,
    };

    // Aggregated AFTER the individual tables — static field initializers run in textual order, so these
    // must follow every table they reference (else they capture nulls). Indexed by piece type 1..6 → 0..5.
    private static readonly int[][] MgPst = { MgPawn, MgKnight, MgBishop, MgRook, MgQueen, MgKing };
    private static readonly int[][] EgPst = { EgPawn, EgKnight, EgBishop, EgRook, EgQueen, EgKing };

    /// <summary>Return the PeSTO PST tables with a learned delta ADDED on top (PeSTO floor + corpus nudge) —
    /// the "blend, don't replace" path (a flat learned table replacing PeSTO regresses hard; PeSTO + a small
    /// learned overlay is the honest way to fold corpus knowledge into the leaf eval). Shapes must match
    /// <c>[6][64]</c>; returns fresh tables (PeSTO stays untouched).</summary>
    public static (int[][] Mg, int[][] Eg) BlendPeStoWith(int[][] addMg, int[][] addEg)
    {
        var mg = new int[6][]; var eg = new int[6][];
        for (int t = 0; t < 6; t++)
        {
            mg[t] = new int[64]; eg[t] = new int[64];
            for (int i = 0; i < 64; i++)
            {
                mg[t][i] = MgPst[t][i] + (addMg[t][i]);
                eg[t][i] = EgPst[t][i] + (addEg[t][i]);
            }
        }
        return (mg, eg);
    }
}
