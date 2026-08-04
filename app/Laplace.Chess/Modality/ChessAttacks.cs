using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Laplace.Modality.Chess;

/// <summary>
/// Precomputed attack tables — the lookup half of move generation.
///
/// MoveGen resolves attacks by WALKING RAYS at runtime (`while (Board.OnBoard(t))`) over a 0x88
/// mailbox, and IsSquareAttacked repeats that walk for every legality test: ~35 pseudo moves per
/// position, each re-deriving the same geometry. MEASURED: replay is 46.7% of compose time and
/// IsSquareAttacked is its bulk. None of that geometry depends on anything but (square,
/// occupancy), so all of it is a table.
///
/// Layout, and why these sizes:
///   knight / king / pawn   64 entries each, dense — leapers have no occupancy dependence
///   bishop / rook          occupancy-indexed; the index is the RELEVANT occupancy only
///                          (interior ray squares; a blocker on the ray's last square changes
///                          nothing beyond it, so edges are excluded from the mask)
///   between / line         64x64, for pin and check-evasion masks
///
/// Sliding index uses BMI2 PEXT when the CPU has it — this box is Broadwell-E (bmi2/avx2/popcnt
/// confirmed), where PEXT is a 3-cycle hardware instruction. On AMD Zen 1/2 PEXT is microcoded
/// and far slower than multiply-shift magics, so the portable fallback walks the ray directly
/// rather than pretending one strategy fits every host. Correctness is identical either way;
/// only the index derivation differs.
///
/// IDENTITY-NEUTRAL. This computes the same move sets the mailbox generator computes, so nothing
/// here can move a hash. Perft is the gate: Startpos d6 = 119,060,324 and Kiwipete d5 =
/// 193,690,690 fail loudly on a single wrong bit.
/// </summary>
public static class ChessAttacks
{
    private static readonly ulong[] KnightTbl = new ulong[64];
    private static readonly ulong[] KingTbl = new ulong[64];
    private static readonly ulong[] WPawnTbl = new ulong[64];
    private static readonly ulong[] BPawnTbl = new ulong[64];

    private static readonly ulong[] BishopMask = new ulong[64];
    private static readonly ulong[] RookMask = new ulong[64];
    private static readonly int[] BishopBase = new int[64];
    private static readonly int[] RookBase = new int[64];
    private static readonly ulong[] SlideTbl;

    private static readonly ulong[] BetweenTbl = new ulong[64 * 64];
    private static readonly ulong[] LineTbl = new ulong[64 * 64];

    private static readonly bool UsePext = Bmi2.X64.IsSupported;

    // (file, rank) deltas. Rook/bishop split so a queen is just the union.
    private static readonly (int df, int dr)[] RookDirs = { (0, 1), (0, -1), (1, 0), (-1, 0) };
    private static readonly (int df, int dr)[] BishopDirs = { (1, 1), (1, -1), (-1, 1), (-1, -1) };
    private static readonly (int df, int dr)[] KnightDirs =
        { (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2) };
    private static readonly (int df, int dr)[] KingDirs =
        { (0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    static ChessAttacks()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            KnightTbl[sq] = Leaper(sq, KnightDirs);
            KingTbl[sq] = Leaper(sq, KingDirs);
            WPawnTbl[sq] = Leaper(sq, new[] { (1, 1), (-1, 1) });
            BPawnTbl[sq] = Leaper(sq, new[] { (1, -1), (-1, -1) });
            BishopMask[sq] = RelevantMask(sq, BishopDirs);
            RookMask[sq] = RelevantMask(sq, RookDirs);
        }

        // Dense occupancy-indexed slider table. Size is the sum over squares of 2^popcount(mask):
        // 5,248 bishop entries + 102,400 rook entries = 107,648 ulongs ≈ 861 KB.
        int total = 0;
        for (int sq = 0; sq < 64; sq++)
        {
            BishopBase[sq] = total;
            total += 1 << BitOperations.PopCount(BishopMask[sq]);
        }
        for (int sq = 0; sq < 64; sq++)
        {
            RookBase[sq] = total;
            total += 1 << BitOperations.PopCount(RookMask[sq]);
        }
        SlideTbl = new ulong[total];

        for (int sq = 0; sq < 64; sq++)
        {
            FillSlider(sq, BishopMask[sq], BishopBase[sq], BishopDirs);
            FillSlider(sq, RookMask[sq], RookBase[sq], RookDirs);
        }

        BuildBetweenAndLine();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Knight(int sq) => KnightTbl[sq];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong King(int sq) => KingTbl[sq];

    /// <summary>Squares a pawn of this colour ATTACKS from <paramref name="sq"/> (not pushes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Pawn(int sq, bool white) => white ? WPawnTbl[sq] : BPawnTbl[sq];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bishop(int sq, ulong occ) =>
        SlideTbl[BishopBase[sq] + Index(occ, BishopMask[sq])];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Rook(int sq, ulong occ) =>
        SlideTbl[RookBase[sq] + Index(occ, RookMask[sq])];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Queen(int sq, ulong occ) => Bishop(sq, occ) | Rook(sq, occ);

    /// <summary>Squares strictly between two squares on a shared rank, file or diagonal; 0 otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Between(int a, int b) => BetweenTbl[(a << 6) | b];

    /// <summary>The full line through two squares (both endpoints included); 0 if not aligned.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Line(int a, int b) => LineTbl[(a << 6) | b];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Index(ulong occ, ulong mask) =>
        UsePext
            ? (int)Bmi2.X64.ParallelBitExtract(occ, mask)
            : PortableIndex(occ, mask);

    // Same index PEXT produces, without the instruction: pack the mask's set bits in order.
    private static int PortableIndex(ulong occ, ulong mask)
    {
        int idx = 0, bit = 0;
        while (mask != 0)
        {
            ulong ls1b = mask & (ulong)(-(long)mask);
            if ((occ & ls1b) != 0) idx |= 1 << bit;
            mask &= mask - 1;
            bit++;
        }
        return idx;
    }

    private static ulong Leaper(int sq, (int df, int dr)[] dirs)
    {
        ulong r = 0;
        int f0 = sq & 7, r0 = sq >> 3;
        foreach (var (df, dr) in dirs)
        {
            int f = f0 + df, k = r0 + dr;
            if ((uint)f < 8 && (uint)k < 8) r |= 1UL << ((k << 3) | f);
        }
        return r;
    }

    /// <summary>
    /// Occupancy bits that can change this square's slider attacks: every ray square EXCEPT the
    /// last reachable one. A blocker sitting on the board edge blocks nothing beyond itself, so
    /// including it would double the table for no information.
    /// </summary>
    private static ulong RelevantMask(int sq, (int df, int dr)[] dirs)
    {
        ulong r = 0;
        int f0 = sq & 7, r0 = sq >> 3;
        foreach (var (df, dr) in dirs)
        {
            int f = f0 + df, k = r0 + dr;
            while ((uint)(f + df) < 8 && (uint)(k + dr) < 8)
            {
                r |= 1UL << ((k << 3) | f);
                f += df; k += dr;
            }
        }
        return r;
    }

    private static ulong SlideAttacks(int sq, ulong occ, (int df, int dr)[] dirs)
    {
        ulong r = 0;
        int f0 = sq & 7, r0 = sq >> 3;
        foreach (var (df, dr) in dirs)
        {
            int f = f0 + df, k = r0 + dr;
            while ((uint)f < 8 && (uint)k < 8)
            {
                int t = (k << 3) | f;
                r |= 1UL << t;
                if ((occ & (1UL << t)) != 0) break;   // inclusive: the blocker is capturable
                f += df; k += dr;
            }
        }
        return r;
    }

    // Carry-rippler subset enumeration: visits every subset of `mask` exactly once.
    private static void FillSlider(int sq, ulong mask, int baseIdx, (int df, int dr)[] dirs)
    {
        ulong sub = 0;
        do
        {
            SlideTbl[baseIdx + Index(sub, mask)] = SlideAttacks(sq, sub, dirs);
            sub = (sub - mask) & mask;
        } while (sub != 0);
    }

    private static void BuildBetweenAndLine()
    {
        for (int a = 0; a < 64; a++)
        {
            int fa = a & 7, ra = a >> 3;
            foreach (var (df, dr) in KingDirs)   // all 8 ray directions
            {
                ulong run = 0;
                int f = fa + df, k = ra + dr;
                while ((uint)f < 8 && (uint)k < 8)
                {
                    int b = (k << 3) | f;
                    BetweenTbl[(a << 6) | b] = run;                 // squares strictly between
                    run |= 1UL << b;                                // b joins the run for the next step
                    f += df; k += dr;
                }

                // The full line through a in this direction, both ways, endpoints included.
                ulong line = (1UL << a) | Ray(a, df, dr) | Ray(a, -df, -dr);
                f = fa + df; k = ra + dr;
                while ((uint)f < 8 && (uint)k < 8)
                {
                    LineTbl[(a << 6) | ((k << 3) | f)] = line;
                    f += df; k += dr;
                }
            }
        }
    }

    private static ulong Ray(int sq, int df, int dr)
    {
        ulong r = 0;
        int f = (sq & 7) + df, k = (sq >> 3) + dr;
        while ((uint)f < 8 && (uint)k < 8)
        {
            r |= 1UL << ((k << 3) | f);
            f += df; k += dr;
        }
        return r;
    }
}
