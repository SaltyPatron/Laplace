namespace Laplace.Modality.Chess;

/// <summary>
/// Precomputed castling geometry: for every (king file, rook file) pair, which squares of
/// the home rank must be empty and which the king traverses: two table reads and one AND
/// per castle.
///
/// Castling destinations are fixed (king to g/c, rook to f/d) in Chess960 as in chess, so
/// every square either piece crosses is a pure function of the two starting files: 64
/// entries, computed once.
///
/// Keyed on files, not on a Chess960 position number: Double Fischer Random gives the two
/// sides different arrays with no position number, and a source may ship an unnumbered
/// arrangement. The rules constrain only the king and rook files, so 64 entries cover every
/// legal arrangement.
///
/// Everything castling touches is on the mover's home rank, so a file mask is one byte, not
/// a 64-bit board mask. The occupancy test is
/// <c>(occupiedFiles &amp; mustBeEmpty) != 0</c>.
/// </summary>
internal static class CastlePaths
{
    /// <summary>King destination file when castling. Fixed for chess and for Chess960.</summary>
    internal const int KingSideKingFile = 6;   // g
    internal const int QueenSideKingFile = 2;  // c
    internal const int KingSideRookFile = 5;   // f
    internal const int QueenSideRookFile = 3;  // d

    // [kingFile, rookFile]. Entries where the two coincide are unused (a rook cannot start
    // on the king's square) and stay zero.
    private static readonly byte[,] MustBeEmpty = new byte[8, 8];
    private static readonly byte[,] KingTraverses = new byte[8, 8];

    static CastlePaths()
    {
        for (int king = 0; king < 8; king++)
        for (int rook = 0; rook < 8; rook++)
        {
            if (king == rook) continue;
            bool kingSide = rook > king;
            int kingTo = kingSide ? KingSideKingFile : QueenSideKingFile;
            int rookTo = kingSide ? KingSideRookFile : QueenSideRookFile;

            byte kingSpan = Span(king, kingTo);
            byte rookSpan = Span(rook, rookTo);

            // The two castling pieces do not block each other (both move), so their own
            // starting files are excluded from the emptiness requirement.
            int occupied = (kingSpan | rookSpan) & ~(1 << king) & ~(1 << rook);
            MustBeEmpty[king, rook] = (byte)occupied;

            // The king may not start in, pass through, or land on check, so its origin is
            // included; a king that castles without moving has exactly one square to check.
            KingTraverses[king, rook] = kingSpan;
        }
    }

    /// <summary>Files that must be empty for this castle, as an eight-bit mask.</summary>
    internal static byte EmptyMask(int kingFile, int rookFile) => MustBeEmpty[kingFile, rookFile];

    /// <summary>Files the king occupies at some point, all of which must be unattacked.</summary>
    internal static byte KingPathMask(int kingFile, int rookFile) => KingTraverses[kingFile, rookFile];

    /// <summary>Occupied files of one rank, as an eight-bit mask.</summary>
    internal static byte OccupiedFiles(Board b, int rank)
    {
        int mask = 0;
        for (int f = 0; f < 8; f++)
            if (b.Squares[Board.Sq(f, rank)] != Piece.Empty) mask |= 1 << f;
        return (byte)mask;
    }

    /// <summary>Inclusive file span between two files, as an eight-bit mask.</summary>
    private static byte Span(int a, int b)
    {
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        int mask = 0;
        for (int f = lo; f <= hi; f++) mask |= 1 << f;
        return (byte)mask;
    }
}
