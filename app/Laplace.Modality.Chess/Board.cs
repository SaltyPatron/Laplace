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

    public Board Clone()
    {
        var b = new Board
        {
            WhiteToMove = WhiteToMove,
            Castle = Castle,
            EpSquare = EpSquare,
            HalfmoveClock = HalfmoveClock,
            FullmoveNumber = FullmoveNumber,
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

        b.Castle = CastleRights.None;
        if (parts[2] != "-")
        {
            foreach (char c in parts[2])
            {
                b.Castle |= c switch
                {
                    'K' => CastleRights.WhiteKing,
                    'Q' => CastleRights.WhiteQueen,
                    'k' => CastleRights.BlackKing,
                    'q' => CastleRights.BlackQueen,
                    _ => CastleRights.None,
                };
            }
        }

        b.EpSquare = parts[3] == "-" ? -1 : AlgebraicToSquare(parts[3]);
        b.HalfmoveClock = parts.Length > 4 ? int.Parse(parts[4]) : 0;
        b.FullmoveNumber = parts.Length > 5 ? int.Parse(parts[5]) : 1;
        return b;
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

    public string CastleString()
    {
        if (Castle == CastleRights.None) return "-";
        var sb = new StringBuilder();
        if ((Castle & CastleRights.WhiteKing) != 0) sb.Append('K');
        if ((Castle & CastleRights.WhiteQueen) != 0) sb.Append('Q');
        if ((Castle & CastleRights.BlackKing) != 0) sb.Append('k');
        if ((Castle & CastleRights.BlackQueen) != 0) sb.Append('q');
        return sb.ToString();
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
