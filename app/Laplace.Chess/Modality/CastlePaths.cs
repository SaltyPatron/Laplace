namespace Laplace.Modality.Chess;

/// <summary>
/// Precomputed castling geometry: for every (king file, rook file) pair, which squares of
/// the home rank must be EMPTY and which the king TRAVERSES. Two table reads and one AND,
/// instead of walking squares per call.
///
/// WHY THIS IS A TABLE. Castling destinations are fixed — king to g/c, rook to f/d — in
/// Chess960 exactly as in chess. So once you know where the king and its rook START, every
/// square either of them crosses is determined, forever. That is a pure function of two
/// files, i.e. 64 entries, computed once. The first cut walked those squares with a loop on
/// every generated move; this is the same answer as a lookup, which is what the legal-move
/// bitmask machinery next door (<see cref="Bitboards"/>) already does for everything else.
///
/// KEYED ON FILES, NOT ON THE 960. The obvious index is the position number, and it is the
/// wrong one: it would cover only the standard enumeration. Double Fischer Random gives the
/// two sides different arrays and has no position number at all, and any source may ship an
/// arrangement nobody numbered. The RULES are the authority — a king somewhere between two
/// rooks — so the key is what the rules actually constrain. 64 entries cover all 960, all
/// 921,600 DFRC pairs, and anything off-list.
///
/// ONE RANK, SO ONE BYTE. Everything castling touches is on the mover's home rank, so a
/// file mask is eight bits, not a 64-bit board mask. The occupancy test is
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

            // The two castling pieces do not block each other — they both move — so their
            // own starting files come out of the emptiness requirement. Everything else on
            // either path must be clear.
            int occupied = (kingSpan | rookSpan) & ~(1 << king) & ~(1 << rook);
            MustBeEmpty[king, rook] = (byte)occupied;

            // The king may not start in, pass through, or land on check. Its origin is
            // included: the caller would otherwise have to test it separately, and a king
            // that castles without moving (origin == destination) has exactly one square
            // to check.
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
