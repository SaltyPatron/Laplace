using System.Collections.Frozen;

namespace Laplace.Modality.Chess;

/// <summary>
/// The 960 Chess960 ("Freestyle") starting arrays, by their canonical SP number — the
/// Scharnagl numbering chess.com, Lichess and FIDE all use. Standard chess is SP 518.
///
/// THIS IS A NAMING TABLE, NOT A VALIDATOR, AND THE DIFFERENCE IS THE WHOLE POINT.
///
/// The 960 is SOMEBODY ELSE'S ENUMERATION — Scharnagl's, which chess.com, Lichess and FIDE
/// adopted. What makes a Freestyle position legal is the RULES (bishops on opposite
/// colours, king between the rooks), not membership of anyone's list. The two coincide for
/// symmetric single-array Chess960, and they stop coinciding the moment a source ships
/// something the list does not cover — Double Fischer Random, where White and Black get
/// DIFFERENT back ranks, is legal under the format and has no SP number at all.
///
/// So membership decides whether we can NAME the variant, never whether we can play it.
/// Nothing in the replay path consults this table; castling geometry comes off the board's
/// rook files and works for any arrangement. A position outside the enumeration replays
/// exactly the same and simply carries no board number — unattested, which is not
/// attested-false. Treating a missing number as a corrupt record would be this codebase's
/// own EXISTS-collapses-the-distinction mistake in a new place.
///
/// What it is for: PROVENANCE. "Freestyle #376" is the game's variant, named, in one
/// number rather than a back-rank string — a fact worth attesting when it exists.
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
    /// The SP number of a board that is a Chess960 STARTING array, or null when there is no
    /// number to give: a mid-game position, an asymmetric (Double Fischer Random) start, or
    /// any legal arrangement outside the standard enumeration.
    ///
    /// Null means "no name for this", not "reject this". The caller attests the number when
    /// it exists and attests nothing when it does not.
    ///
    /// A numbered start is: both back ranks the SAME arrangement, both pawn ranks full,
    /// nothing anywhere else. Checking the whole board rather than just rank 1 is
    /// deliberate — otherwise a middlegame that happens to have an intact back rank reports
    /// a variant it was never played under.
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
