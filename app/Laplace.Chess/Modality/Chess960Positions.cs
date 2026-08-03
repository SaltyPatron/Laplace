using System.Collections.Frozen;

namespace Laplace.Modality.Chess;

/// <summary>
/// The 960 Chess960 ("Freestyle") starting arrays, by their canonical SP number — the
/// Scharnagl numbering chess.com, Lichess and FIDE all use. Standard chess is SP 518.
///
/// WHY A TABLE AND NOT A PREDICATE. The set is fixed, small and computable, which makes it
/// worth having in one place rather than re-deriving a legality rule (bishops on opposite
/// colours, king between the rooks) at each call site. Two things fall out that a predicate
/// does not give:
///
///   VALIDATION. A back rank that is not in this set is not a Freestyle position. Without
///   it a corrupt or hand-edited FEN replays happily as "some 960 game" and is recorded as
///   fact. Membership is the existence test, the same way an id is everywhere else here.
///
///   PROVENANCE. The SP number is the game's variant, named. "Freestyle #376" is a fact
///   about the game worth recording, and it is one number rather than a back-rank string.
///
/// DERIVED, NOT TYPED OUT. 960 literals would be 960 chances to fat-finger one, and the
/// derivation is eight lines. Built once on first use and frozen; the reverse map is the
/// O(1) direction, which is the one every caller wants.
///
/// The engine does NOT key on these. Castling geometry lives on the Board as rook files,
/// because a MID-GAME position (ChessPositionRef composes ids from arbitrary FENs) carries
/// its castling rook files but not its starting back rank — position #376 at move 40 looks
/// like nothing in this table. SP is a start-position fact; rook files are a position fact.
/// </summary>
public static class Chess960Positions
{
    /// <summary>Standard chess. Its back rank is RNBQKBNR.</summary>
    public const int StandardNumber = 518;

    public const int Count = 960;

    // The ten arrangements of K, R, R, N, N once the bishops and queen are placed. The king
    // is always between the rooks, which is what makes castling well defined for all 960.
    private static readonly string[] KrnPatterns =
        ["NNRKR", "NRNKR", "NRKNR", "NRKRN", "RNNKR", "RNKNR", "RNKRN", "RKNNR", "RKNRN", "RKRNN"];

    private static readonly Lazy<(string[] ByNumber, FrozenDictionary<string, int> ByRank)> Table =
        new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The back rank of a position number, uppercase (white's view).</summary>
    public static string BackRank(int number)
    {
        if ((uint)number >= Count) throw new ArgumentOutOfRangeException(nameof(number));
        return Table.Value.ByNumber[number];
    }

    /// <summary>
    /// The SP number of a back rank, or null when the arrangement is not one of the 960.
    /// O(1) — a frozen dictionary probe, which is the direction callers actually need.
    /// </summary>
    public static int? TryNumber(string backRank)
        => Table.Value.ByRank.TryGetValue(backRank, out int n) ? n : null;

    /// <summary>
    /// The SP number of a board that is a Chess960 STARTING array, or null when it is not
    /// one — including when it is a mid-game position, which has no SP number at all.
    ///
    /// A starting array is: both back ranks the same arrangement, both pawn ranks full, and
    /// nothing anywhere else. Checking the whole board rather than just rank 1 is the point
    /// — otherwise a middlegame that happens to have an intact back rank reports a variant
    /// it was never played under.
    /// </summary>
    public static int? TryNumberOfStart(Board b)
    {
        Span<char> white = stackalloc char[8];
        for (int f = 0; f < 8; f++)
        {
            var wp = b.Squares[Board.Sq(f, 0)];
            var bp = b.Squares[Board.Sq(f, 7)];
            if (wp == Piece.Empty || (sbyte)wp < 0) return null;
            if (bp == Piece.Empty || (sbyte)bp > 0) return null;
            if (Board.TypeOf(wp) != Board.TypeOf(bp)) return null;      // mirrored
            if (b.Squares[Board.Sq(f, 1)] != Piece.WPawn) return null;
            if (b.Squares[Board.Sq(f, 6)] != Piece.BPawn) return null;
            for (int r = 2; r <= 5; r++)
                if (b.Squares[Board.Sq(f, r)] != Piece.Empty) return null;
            white[f] = Board.PieceToChar(wp);
        }
        return TryNumber(new string(white));
    }

    private static (string[], FrozenDictionary<string, int>) Build()
    {
        var byNumber = new string[Count];
        var byRank = new Dictionary<string, int>(Count, StringComparer.Ordinal);
        for (int n = 0; n < Count; n++)
        {
            string rank = Derive(n);
            byNumber[n] = rank;
            byRank[rank] = n;
        }
        return (byNumber, byRank.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>
    /// Scharnagl's derivation. The number is read as a mixed-radix digit string: light
    /// bishop (4), dark bishop (4), queen among the remaining six (6), then one of ten
    /// knight/rook/king patterns in the five squares left.
    /// </summary>
    private static string Derive(int n)
    {
        int q = Math.DivRem(n, 4, out int b1);
        int r = Math.DivRem(q, 4, out int b2);
        int s = Math.DivRem(r, 6, out int qi);

        var files = new char[8];
        files[2 * b1 + 1] = 'B';   // light squares: b d f h
        files[2 * b2] = 'B';       // dark squares:  a c e g
        files[NthFree(files, qi)] = 'Q';

        string krn = KrnPatterns[s];
        for (int i = 0; i < krn.Length; i++)
            files[NthFree(files, 0)] = krn[i];
        return new string(files);
    }

    private static int NthFree(char[] files, int index)
    {
        for (int f = 0; f < 8; f++)
        {
            if (files[f] != '\0') continue;
            if (index-- == 0) return f;
        }
        throw new InvalidOperationException("Chess960 derivation ran out of free files");
    }
}
