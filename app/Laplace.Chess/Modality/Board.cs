using System.Text;

namespace Laplace.Modality.Chess;

public enum Piece : sbyte
{
    Empty = 0,
    WPawn = 1, WKnight = 2, WBishop = 3, WRook = 4, WQueen = 5, WKing = 6,
    BPawn = -1, BKnight = -2, BBishop = -3, BRook = -4, BQueen = -5, BKing = -6,
}

[Flags]
public enum CastleRights : byte
{
    None = 0,
    WhiteKing = 1,
    WhiteQueen = 2,
    BlackKing = 4,
    BlackQueen = 8,
    All = WhiteKing | WhiteQueen | BlackKing | BlackQueen,
}

public sealed class Board
{
    public readonly Piece[] Squares = new Piece[128];
    public bool WhiteToMove;
    public CastleRights Castle;
    public int EpSquare;
    public int HalfmoveClock;
    public int FullmoveNumber;

    /// <summary>
    /// The FILE each castling rook started on. Chess960 (chess.com "Freestyle") shuffles
    /// the back rank, so "the h-rook" is not a constant — X-FEN/Shredder writes the files
    /// into the castling field ("FCfc") precisely because KQkq cannot express them.
    ///
    /// IDENTITY IS UNAFFECTED FOR STANDARD CHESS, AND THAT IS LOAD-BEARING. These default
    /// to the standard files, and <see cref="CastleString"/> emits the classic KQkq
    /// whenever they hold — so a standard position's content surface
    /// (PositionContent.Surface, which embeds CastleString) is byte-identical to what it
    /// was before Chess960 existed here. Same surface, same content id, no reseed. Only
    /// positions whose castling rooks are NOT on a/h get new ids, and those are exactly
    /// the positions the substrate does not contain, because these games were refused.
    /// </summary>
    public sbyte WhiteKingRookFile = 7;
    public sbyte WhiteQueenRookFile = 0;
    public sbyte BlackKingRookFile = 7;
    public sbyte BlackQueenRookFile = 0;

    /// <summary>The file the castling rook for this side/flank started on.</summary>
    public int CastleRookFile(bool white, bool kingSide) => white
        ? (kingSide ? WhiteKingRookFile : WhiteQueenRookFile)
        : (kingSide ? BlackKingRookFile : BlackQueenRookFile);

    /// <summary>True when every castling rook is on its standard file — i.e. ordinary chess.</summary>
    public bool StandardCastleFiles =>
        WhiteKingRookFile == 7 && WhiteQueenRookFile == 0
        && BlackKingRookFile == 7 && BlackQueenRookFile == 0;

    public Board Clone()
    {
        var b = new Board
        {
            WhiteToMove = WhiteToMove,
            Castle = Castle,
            EpSquare = EpSquare,
            HalfmoveClock = HalfmoveClock,
            FullmoveNumber = FullmoveNumber,
            WhiteKingRookFile = WhiteKingRookFile,
            WhiteQueenRookFile = WhiteQueenRookFile,
            BlackKingRookFile = BlackKingRookFile,
            BlackQueenRookFile = BlackQueenRookFile,
        };
        Array.Copy(Squares, b.Squares, 128);
        return b;
    }

    public static int Sq(int file, int rank) => rank * 16 + file;
    public static int FileOf(int sq) => sq & 7;
    public static int RankOf(int sq) => sq >> 4;
    public static bool OnBoard(int sq) => (sq & 0x88) == 0;

    public static bool IsWhite(Piece p) => (sbyte)p > 0;
    public static bool IsBlack(Piece p) => (sbyte)p < 0;
    public static Piece TypeOf(Piece p) => (Piece)Math.Abs((sbyte)p);

    public int FindKing(bool white)
    {
        Piece king = white ? Piece.WKing : Piece.BKing;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            if (Squares[sq] == king) return sq;
        }
        return -1;
    }

    public static Board FromFen(string fen)
    {
        var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            throw new FormatException($"Invalid FEN (need >=4 fields): {fen}");

        var b = new Board();
        string placement = parts[0];
        var ranks = placement.Split('/');
        if (ranks.Length != 8) throw new FormatException($"Invalid FEN ranks: {fen}");
        for (int r = 0; r < 8; r++)
        {
            int rank = 7 - r;
            int file = 0;
            foreach (char c in ranks[r])
            {
                if (char.IsDigit(c)) { file += c - '0'; continue; }
                b.Squares[Sq(file, rank)] = CharToPiece(c);
                file++;
            }
            if (file != 8) throw new FormatException($"Invalid FEN rank width: {fen}");
        }

        b.WhiteToMove = parts[1] == "w";

        // CASTLING, INCLUDING CHESS960. Three forms appear in the wild and all three are
        // read here:
        //   KQkq   classic. Resolved to the OUTERMOST rook on that flank, which is the
        //          X-FEN semantic and is a/h in ordinary chess — so nothing moves.
        //   AHah   Shredder: the rook's own file, explicitly.
        //   mixed  X-FEN uses KQkq when unambiguous and a file letter when not.
        //
        // This used to throw on anything but KQkq. That was the right call at the time —
        // replaying a Chess960 game from the standard array records a game that was never
        // played — but it refused 1,866 games (0.8% of the corpus, concentrated in the
        // chess.com archives, every "Freestyle" game). Now they are read.
        b.Castle = CastleRights.None;
        if (parts[2] != "-")
        {
            foreach (char c in parts[2])
            {
                bool white = char.IsUpper(c);
                int rank = white ? 0 : 7;
                char lower = char.ToLowerInvariant(c);
                int kingFile = KingFileOnRank(b, rank);

                int rookFile;
                if (lower == 'k')      rookFile = OutermostRook(b, rank, kingFile, toward: +1, fen);
                else if (lower == 'q') rookFile = OutermostRook(b, rank, kingFile, toward: -1, fen);
                else if (lower >= 'a' && lower <= 'h') rookFile = lower - 'a';
                else throw new FormatException(
                    $"Unsupported castling availability '{c}' in FEN '{fen}'.");

                if (kingFile < 0)
                    throw new FormatException(
                        $"Castling right '{c}' in FEN '{fen}' but no king on that rank.");

                // Which flank a FILE letter names is decided by the king, not by the letter:
                // a rook left of the king is queen-side however it is spelled.
                bool kingSide = rookFile > kingFile;
                if (white)
                {
                    b.Castle |= kingSide ? CastleRights.WhiteKing : CastleRights.WhiteQueen;
                    if (kingSide) b.WhiteKingRookFile = (sbyte)rookFile;
                    else          b.WhiteQueenRookFile = (sbyte)rookFile;
                }
                else
                {
                    b.Castle |= kingSide ? CastleRights.BlackKing : CastleRights.BlackQueen;
                    if (kingSide) b.BlackKingRookFile = (sbyte)rookFile;
                    else          b.BlackQueenRookFile = (sbyte)rookFile;
                }
            }
        }

        b.EpSquare = parts[3] == "-" ? -1 : AlgebraicToSquare(parts[3]);

        // Named, not raw. A bare int.Parse here reports ".. near offset N. Expected an ASCII
        // digit" with no clue which field or which game, which is what a malformed counter in
        // one record out of 190,705 used to look like.
        b.HalfmoveClock = ParseCounter(parts, 4, 0, "halfmove clock", fen);
        b.FullmoveNumber = ParseCounter(parts, 5, 1, "fullmove number", fen);
        return b;
    }

    private static int ParseCounter(string[] parts, int index, int fallback, string field, string fen)
    {
        if (parts.Length <= index) return fallback;
        if (int.TryParse(parts[index], out int v)) return v;
        throw new FormatException($"Invalid {field} '{parts[index]}' in FEN '{fen}'");
    }

    public string ToFen()
    {
        var sb = new StringBuilder();
        for (int rank = 7; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                var p = Squares[Sq(file, rank)];
                if (p == Piece.Empty) { empty++; continue; }
                if (empty > 0) { sb.Append(empty); empty = 0; }
                sb.Append(PieceToChar(p));
            }
            if (empty > 0) sb.Append(empty);
            if (rank > 0) sb.Append('/');
        }
        sb.Append(' ').Append(WhiteToMove ? 'w' : 'b').Append(' ');
        sb.Append(CastleString());
        sb.Append(' ').Append(EpSquare < 0 ? "-" : SquareToAlgebraic(EpSquare));
        sb.Append(' ').Append(HalfmoveClock);
        sb.Append(' ').Append(FullmoveNumber);
        return sb.ToString();
    }

    /// <summary>
    /// The castling field. KQkq while the rooks are on their standard files — which is
    /// ALWAYS true of ordinary chess, so this is byte-identical to the pre-Chess960
    /// output and position identity does not move. Shredder file letters otherwise.
    /// </summary>
    public string CastleString()
    {
        if (Castle == CastleRights.None) return "-";
        var sb = new StringBuilder(4);
        bool std = StandardCastleFiles;
        if ((Castle & CastleRights.WhiteKing) != 0)
            sb.Append(std ? 'K' : char.ToUpperInvariant(FileChar(WhiteKingRookFile)));
        if ((Castle & CastleRights.WhiteQueen) != 0)
            sb.Append(std ? 'Q' : char.ToUpperInvariant(FileChar(WhiteQueenRookFile)));
        if ((Castle & CastleRights.BlackKing) != 0)
            sb.Append(std ? 'k' : FileChar(BlackKingRookFile));
        if ((Castle & CastleRights.BlackQueen) != 0)
            sb.Append(std ? 'q' : FileChar(BlackQueenRookFile));
        return sb.ToString();
    }

    private static char FileChar(int file) => (char)('a' + file);

    private static int KingFileOnRank(Board b, int rank)
    {
        Piece king = rank == 0 ? Piece.WKing : Piece.BKing;
        for (int f = 0; f < 8; f++)
            if (b.Squares[Sq(f, rank)] == king) return f;
        return -1;
    }

    /// <summary>
    /// The outermost rook of this colour on <paramref name="rank"/>, scanning away from the
    /// king in <paramref name="toward"/>. This is what a bare K/Q means under X-FEN, and in
    /// ordinary chess it lands on h/a — which is why classic FENs keep behaving exactly as
    /// they did.
    /// </summary>
    private static int OutermostRook(Board b, int rank, int kingFile, int toward, string fen)
    {
        if (kingFile < 0) return toward > 0 ? 7 : 0;
        Piece rook = rank == 0 ? Piece.WRook : Piece.BRook;
        int found = -1;
        for (int f = kingFile + toward; f >= 0 && f < 8; f += toward)
            if (b.Squares[Sq(f, rank)] == rook) found = f;
        if (found < 0)
            throw new FormatException(
                $"Castling right implies a rook {(toward > 0 ? "right" : "left")} of the king "
                + $"on rank {rank + 1}, and there is none, in FEN '{fen}'.");
        return found;
    }


    public static Piece CharToPiece(char c) => c switch
    {
        'P' => Piece.WPawn,
        'N' => Piece.WKnight,
        'B' => Piece.WBishop,
        'R' => Piece.WRook,
        'Q' => Piece.WQueen,
        'K' => Piece.WKing,
        'p' => Piece.BPawn,
        'n' => Piece.BKnight,
        'b' => Piece.BBishop,
        'r' => Piece.BRook,
        'q' => Piece.BQueen,
        'k' => Piece.BKing,
        _ => throw new FormatException($"Invalid piece char: {c}"),
    };

    public static char PieceToChar(Piece p) => p switch
    {
        Piece.WPawn => 'P',
        Piece.WKnight => 'N',
        Piece.WBishop => 'B',
        Piece.WRook => 'R',
        Piece.WQueen => 'Q',
        Piece.WKing => 'K',
        Piece.BPawn => 'p',
        Piece.BKnight => 'n',
        Piece.BBishop => 'b',
        Piece.BRook => 'r',
        Piece.BQueen => 'q',
        Piece.BKing => 'k',
        _ => '.',
    };

    public static int AlgebraicToSquare(string s)
    {
        int file = s[0] - 'a';
        int rank = s[1] - '1';
        return Sq(file, rank);
    }

    public static string SquareToAlgebraic(int sq)
        => $"{(char)('a' + FileOf(sq))}{(char)('1' + RankOf(sq))}";
}
