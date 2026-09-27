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

    // Piece bitboards, kept in step with Squares so the attack tables (ChessAttacks) can index
    // them. Layout matches Bitboards.Of: slot = (int)piece + 6, bit = (rank << 3) | file.
    // Occupied is derived on read, never stored, so it cannot go stale.
    //
    // Maintained only through Set(). Squares stays public for reads (See probes hypothetical
    // occupancies over the raw array; the mailbox generator walks it), but every write must go
    // through Set or the two representations diverge without any error, only wrong move
    // generation. BoardBitboardConsistencyTests re-checks after every make and unmake in perft.
    private readonly ulong[] _bb = new ulong[13];

    public ulong PieceBB(Piece p) => _bb[(int)p + 6];
    public ulong WhiteBB => _bb[(int)Piece.WPawn + 6] | _bb[(int)Piece.WKnight + 6] | _bb[(int)Piece.WBishop + 6]
                          | _bb[(int)Piece.WRook + 6] | _bb[(int)Piece.WQueen + 6] | _bb[(int)Piece.WKing + 6];
    public ulong BlackBB => _bb[(int)Piece.BPawn + 6] | _bb[(int)Piece.BKnight + 6] | _bb[(int)Piece.BBishop + 6]
                          | _bb[(int)Piece.BRook + 6] | _bb[(int)Piece.BQueen + 6] | _bb[(int)Piece.BKing + 6];
    public ulong OccupiedBB => WhiteBB | BlackBB;

    /// <summary>Snapshot of maintained piece bitboards (13 slots). Hot path for compose identity.</summary>
    public Bitboards CopyBitboards()
    {
        var bb = new ulong[13];
        Array.Copy(_bb, bb, 13);
        return Bitboards.FromRaw(bb);
    }

    /// <summary>
    /// The only way to change a square. Clears the outgoing piece's bit and sets the incoming
    /// one, so Squares and the bitboards move together.
    /// </summary>
    public void Set(int sq0x88, Piece p)
    {
        Piece old = Squares[sq0x88];
        if (old == p) return;
        int bit = (RankOf(sq0x88) << 3) | FileOf(sq0x88);
        ulong m = 1UL << bit;
        if (old != Piece.Empty) _bb[(int)old + 6] &= ~m;
        if (p != Piece.Empty) _bb[(int)p + 6] |= m;
        Squares[sq0x88] = p;
    }

    /// <summary>Rebuild the bitboards from Squares. For construction paths and consistency checks.</summary>
    public void RebuildBitboards()
    {
        Array.Clear(_bb, 0, _bb.Length);
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = Squares[sq];
            if (p == Piece.Empty) continue;
            _bb[(int)p + 6] |= 1UL << ((RankOf(sq) << 3) | FileOf(sq));
        }
    }

    /// <summary>True when the maintained bitboards still agree with Squares. Test-facing.</summary>
    public bool BitboardsConsistent()
    {
        var expect = new ulong[13];
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = Squares[sq];
            if (p == Piece.Empty) continue;
            expect[(int)p + 6] |= 1UL << ((RankOf(sq) << 3) | FileOf(sq));
        }
        for (int i = 0; i < 13; i++) if (expect[i] != _bb[i]) return false;
        return true;
    }
    public bool WhiteToMove;
    public CastleRights Castle;
    public int EpSquare;
    public int HalfmoveClock;
    public int FullmoveNumber;

    /// <summary>
    /// The file each castling rook started on. Chess960 shuffles the back rank, so the
    /// castling rook's file is not a constant; X-FEN/Shredder writes it into the castling
    /// field ("FCfc") because KQkq cannot express it.
    ///
    /// These default to the standard files, and <see cref="CastleString"/> emits KQkq
    /// whenever they hold, so a standard position's content surface (PositionContent.Surface,
    /// which embeds CastleString) and therefore its content id do not depend on this field.
    /// Only positions whose castling rooks are off a/h have surfaces naming the files.
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
        // Copy the maintained bitboards too; copying Squares alone would leave the clone's
        // bitboards empty while its Squares are full.
        Array.Copy(_bb, b._bb, _bb.Length);
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
                b.Set(Sq(file, rank), CharToPiece(c));
                file++;
            }
            if (file != 8) throw new FormatException($"Invalid FEN rank width: {fen}");
        }

        b.WhiteToMove = parts[1] == "w";

        // Castling field, including Chess960. Three forms are read:
        //   KQkq   classic. Resolved to the outermost rook on that flank (the X-FEN
        //          meaning), which is a/h in ordinary chess.
        //   AHah   Shredder: the rook's own file, explicitly.
        //   mixed  X-FEN uses KQkq when unambiguous and a file letter when not.
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

        // Parsed with the field name and the FEN, so a malformed counter reports which
        // field of which position failed.
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
    /// The castling field: KQkq while the rooks are on their standard files, as in every
    /// ordinary chess position, so standard position identity is unaffected by Chess960
    /// support; Shredder file letters otherwise.
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
    /// king in <paramref name="toward"/>. This is what a bare K/Q means under X-FEN; in
    /// ordinary chess it is the h/a rook.
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
