namespace Laplace.Modality.Chess;

/// <summary>
/// Zobrist hashing of a <see cref="Board"/> position — the transposition-table key for
/// <see cref="Search"/>. Keys are generated once from a FIXED seed (deterministic across runs, so search
/// and its tests are reproducible). The hash folds piece placement, side to move, castling rights, and
/// the en-passant file. Pure C#, no DB/native.
/// </summary>
public static class Zobrist
{
    private static readonly ulong[,] PieceKeys = new ulong[13, 64]; // [(sbyte)piece + 6][bit 0..63]
    private static readonly ulong[] CastleKeys = new ulong[16];
    private static readonly ulong[] EpFileKeys = new ulong[8];
    private static readonly ulong SideKey;

    static Zobrist()
    {
        // splitmix64 from a fixed seed → a fixed, reproducible key set.
        ulong s = 0x9E3779B97F4A7C15UL;
        ulong Next()
        {
            s += 0x9E3779B97F4A7C15UL;
            ulong z = s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        for (int p = 0; p < 13; p++)
            for (int sq = 0; sq < 64; sq++)
                PieceKeys[p, sq] = Next();
        for (int i = 0; i < 16; i++) CastleKeys[i] = Next();
        for (int f = 0; f < 8; f++) EpFileKeys[f] = Next();
        SideKey = Next();
    }

    public static ulong Hash(Board b)
    {
        ulong h = 0;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = b.Squares[sq];
            if (p == Piece.Empty) continue;
            int bit = (Board.RankOf(sq) << 3) | Board.FileOf(sq);
            h ^= PieceKeys[(sbyte)p + 6, bit];
        }
        h ^= CastleKeys[(int)b.Castle & 15];
        if (b.EpSquare >= 0) h ^= EpFileKeys[Board.FileOf(b.EpSquare)];
        if (b.WhiteToMove) h ^= SideKey;
        return h;
    }
}
