using System.Numerics;

namespace Laplace.Modality.Chess;

/// <summary>
/// Bitboard view of a <see cref="Board"/>: one 64-bit mask per (color,piece-type), little-endian
/// rank-file (a1 = bit 0, h1 = 7, a8 = 56, h8 = 63). Built once from the 0x88 board (kept for
/// perft-verified legality); these masks are the bit-banging substrate for BOTH fast feature
/// extraction and the position's substructure decomposition. Pure C#, no native, no DB.
/// </summary>
public readonly struct Bitboards
{
    // Indexed by (int)piece + 6  → 0..12, with 6 = empty (unused).
    private readonly ulong[] _bb;

    public ulong Of(Piece p) => _bb[(int)p + 6];
    public ulong White => Of(Piece.WPawn) | Of(Piece.WKnight) | Of(Piece.WBishop) | Of(Piece.WRook) | Of(Piece.WQueen) | Of(Piece.WKing);
    public ulong Black => Of(Piece.BPawn) | Of(Piece.BKnight) | Of(Piece.BBishop) | Of(Piece.BRook) | Of(Piece.BQueen) | Of(Piece.BKing);
    public ulong Occupied => White | Black;

    private Bitboards(ulong[] bb) => _bb = bb;

    public static Bitboards FromBoard(Board b)
    {
        var bb = new ulong[13];
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = b.Squares[sq];
            if (p == Piece.Empty) continue;
            bb[(int)p + 6] |= 1UL << Bit(sq);
        }
        return new Bitboards(bb);
    }

    /// <summary>0x88 square → little-endian rank-file bit index (0..63).</summary>
    public static int Bit(int sq0x88) => (Board.RankOf(sq0x88) << 3) | Board.FileOf(sq0x88);
    public static int FileOfBit(int bit) => bit & 7;
    public static int RankOfBit(int bit) => bit >> 3;

    public const ulong FileA = 0x0101010101010101UL;
    public static ulong FileMask(int file) => FileA << file;
    public static ulong AdjacentFiles(int file) =>
        (file > 0 ? FileMask(file - 1) : 0UL) | (file < 7 ? FileMask(file + 1) : 0UL);

    public static int Count(ulong mask) => BitOperations.PopCount(mask);

    /// <summary>Ascending bit indices set in <paramref name="mask"/>.</summary>
    public static IEnumerable<int> Bits(ulong mask)
    {
        while (mask != 0)
        {
            int b = BitOperations.TrailingZeroCount(mask);
            yield return b;
            mask &= mask - 1;
        }
    }

    // --- pawn-structure features (the classic terms, by mask) ---

    /// <summary>Doubled pawns: pawns beyond the first on any file (count of "extra" pawns).</summary>
    public static int Doubled(ulong pawns)
    {
        int doubled = 0;
        for (int f = 0; f < 8; f++)
        {
            int n = Count(pawns & FileMask(f));
            if (n > 1) doubled += n - 1;
        }
        return doubled;
    }

    /// <summary>Isolated pawns: pawns with no friendly pawn on either adjacent file.</summary>
    public static int Isolated(ulong pawns)
    {
        int iso = 0;
        for (int f = 0; f < 8; f++)
        {
            ulong onFile = pawns & FileMask(f);
            if (onFile != 0 && (pawns & AdjacentFiles(f)) == 0)
                iso += Count(onFile);
        }
        return iso;
    }

    /// <summary>Passed pawns of <paramref name="white"/>: no enemy pawn on the same/adjacent file ahead.</summary>
    public static int Passed(ulong ownPawns, ulong enemyPawns, bool white)
    {
        int passed = 0;
        foreach (int b in Bits(ownPawns))
        {
            int f = FileOfBit(b), r = RankOfBit(b);
            ulong frontSpan = FileMask(f) | AdjacentFiles(f);
            // Keep only ranks strictly ahead of this pawn.
            ulong aheadRanks = white ? RanksAbove(r) : RanksBelow(r);
            // Passed iff no enemy pawn on same/adjacent file ahead AND no friendly pawn ahead on its own
            // file (so the rear pawn of a doubled pair is not counted).
            if ((enemyPawns & frontSpan & aheadRanks) == 0 && (ownPawns & FileMask(f) & aheadRanks) == 0)
                passed++;
        }
        return passed;
    }

    private static ulong RanksAbove(int rank)
    {
        ulong m = 0;
        for (int r = rank + 1; r < 8; r++) m |= 0xFFUL << (r << 3);
        return m;
    }

    private static ulong RanksBelow(int rank)
    {
        ulong m = 0;
        for (int r = rank - 1; r >= 0; r--) m |= 0xFFUL << (r << 3);
        return m;
    }
}
