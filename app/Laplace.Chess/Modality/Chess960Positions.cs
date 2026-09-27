using System.Collections.Frozen;

namespace Laplace.Modality.Chess;

/// <summary>
/// The 960 Chess960 starting arrays by Scharnagl SP number (the numbering chess.com, Lichess
/// and FIDE use); standard chess is SP 518.
///
/// A naming table, not a validator: legality comes from the rules (bishops on opposite
/// colours, king between the rooks), and an arrangement outside the enumeration, such as a
/// Double Fischer Random start with different back ranks per side, replays the same and
/// simply has no number. Absence of a number is unknown, not false. Replay never consults
/// this table; castling geometry comes from the board's rook files, because a mid-game
/// position carries its rook files but not its starting back rank.
///
/// Derived from the numbering, not typed out; built once on first use and frozen, with an
/// O(1) back-rank → number map.
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
    /// One frozen-dictionary probe.
    /// </summary>
    public static int? TryNumber(string backRank)
        => Table.Value.ByRank.TryGetValue(backRank, out int n) ? n : null;

    /// <summary>
    /// The SP number of a board that is a Chess960 STARTING array, or null when there is no
    /// number to give: a mid-game position, an asymmetric (Double Fischer Random) start, or
    /// any legal arrangement outside the standard enumeration.
    ///
    /// Null means "no name for this", not "reject this".
    ///
    /// A numbered start has both back ranks the same arrangement, both pawn ranks full and
    /// nothing anywhere else. The whole board is checked, so a middlegame with an intact back
    /// rank is not given a number.
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

    /// <summary>
    /// The castling geometry of a starting array: where the king and its two rooks begin, and
    /// whether a castle on either flank shares its king destination with a one-square king
    /// move.
    ///
    /// A castle (O-O / O-O-O) and a king step (Kc1 / Kg1) can share (from, to): a king on d1
    /// steps to c1, and the queen-side castle also ends on c1, so a SAN resolver matching by
    /// destination sees two candidates. Half of the 960 arrays can produce this; standard
    /// chess cannot, since its king starts two files from both destinations.
    /// </summary>
    public readonly record struct CastleGeometry(
        int KingFile,
        int KingRookFile,
        int QueenRookFile,
        bool KingSideSharesDestinationWithKingMove,
        bool QueenSideSharesDestinationWithKingMove)
    {
        /// <summary>Either flank's castle shares its king destination with a plain king move on this array.</summary>
        public bool CanCollideWithKingMove
            => KingSideSharesDestinationWithKingMove || QueenSideSharesDestinationWithKingMove;
    }

    /// <summary>King destination when castling — fixed at g/c for Chess960 and for chess.</summary>
    public const int KingSideKingFile = 6;
    public const int QueenSideKingFile = 2;

    /// <summary>Castling geometry of a position number. O(1) over the frozen table.</summary>
    public static CastleGeometry Geometry(int number)
    {
        string rank = BackRank(number);
        int king = rank.IndexOf('K');
        int qRook = rank.IndexOf('R');                 // the rook left of the king
        int kRook = rank.LastIndexOf('R');             // the rook right of the king
        return new CastleGeometry(
            king, kRook, qRook,
            // A king adjacent to the destination can step onto it. A king already on it
            // castles without moving and has no king step there, hence == 1, not <= 1.
            Math.Abs(king - KingSideKingFile) == 1,
            Math.Abs(king - QueenSideKingFile) == 1);
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
