using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Modality.Chess;

/// <summary>
/// Binary structural identity for a chess position. PGN/FEN/state-key text is an interchange
/// format, never a content surface. A position is the ordered composition
/// [rules?, side-to-move, castling-mask, en-passant, occupied piece×square...].
/// </summary>
public static class ChessPositionIdentity
{
    public const byte AtomTier = 1;
    public const byte PositionTier = 2;

    internal const byte SideDomain = 1;
    internal const byte CastlingDomain = 2;
    internal const byte EnPassantDomain = 3;
    internal const byte PieceSquareDomain = 4;
    internal const byte RulesDomain = 5;
    internal const byte CastlingRookOverrideDomain = 6;

    internal readonly record struct Atom(byte Domain, ushort Value, Hash128 Digest, bool HasDigest)
    {
        internal static Atom Scalar(byte domain, ushort value) => new(domain, value, default, false);
        internal static Atom Rule(Hash128 digest) => new(RulesDomain, 0, digest, true);
    }

    /// <summary>
    /// Four destination-right bits: White g/c and Black g/c. Chess960 uses the same final
    /// squares, so its normal castling rule state needs no wider representation.
    /// </summary>
    public static byte CastlingDestinationMask(Board board) => (byte)((byte)board.Castle & 0x0F);

    public static Hash128 PositionId(Board board, ChessVariantRules? rules = null)
    {
        Span<Atom> atoms = stackalloc Atom[40];
        int n = FillAtoms(board, rules ?? ChessVariantRules.Standard, atoms);
        Span<Hash128> ids = stackalloc Hash128[n];
        for (int i = 0; i < n; i++) ids[i] = AtomId(atoms[i]);
        return Hash128.Merkle(PositionTier, ids);
    }

    internal static int FillAtoms(Board board, ChessVariantRules rules, Span<Atom> atoms)
    {
        int n = 0;
        if (!rules.IsStandard)
        {
            string surface = rules.Surface();
            int capacity = Encoding.UTF8.GetMaxByteCount(surface.Length);
            Span<byte> utf8 = capacity <= 512 ? stackalloc byte[capacity] : new byte[capacity];
            int bytes = Encoding.UTF8.GetBytes(surface, utf8);
            atoms[n++] = Atom.Rule(Hash128.Blake3(utf8[..bytes]));
        }

        atoms[n++] = Atom.Scalar(SideDomain, board.WhiteToMove ? (ushort)1 : (ushort)0);
        atoms[n++] = Atom.Scalar(CastlingDomain, CastlingDestinationMask(board));
        if (AmbiguousCastlingRookOverride(board) is { } rookOverride)
            atoms[n++] = Atom.Scalar(CastlingRookOverrideDomain, rookOverride);
        int ep = ChessModality.CapturableEpSquare(board);
        atoms[n++] = Atom.Scalar(EnPassantDomain,
            ep < 0 ? (ushort)64 : (ushort)((Board.RankOf(ep) << 3) | Board.FileOf(ep)));

        var bb = board.CopyBitboards();
        foreach (int bit in Bitboards.Bits(bb.Occupied))
        {
            Piece piece = board.Squares[Board.Sq(Bitboards.FileOfBit(bit), Bitboards.RankOfBit(bit))];
            ushort packed = (ushort)((PieceOrdinal(piece) << 6) | bit);
            atoms[n++] = Atom.Scalar(PieceSquareDomain, packed);
        }
        return n;
    }

    internal static Hash128 AtomId(in Atom atom)
    {
        Span<byte> bytes = stackalloc byte[33];
        int n = FillAtomBytes(atom, bytes);
        Span<Hash128> ids = stackalloc Hash128[n];
        for (int i = 0; i < n; i++) ids[i] = ByteAtoms.Id(bytes[i]);
        return Hash128.Merkle(AtomTier, ids);
    }

    /// <summary>
    /// Encode a typed scalar as byte-atom constituents. Payload bytes become two tagged
    /// nibbles, so every constituent stays in ByteAtoms' 0x80..0xFF structural alphabet.
    /// </summary>
    internal static int FillAtomBytes(in Atom atom, Span<byte> output)
    {
        output[0] = (byte)(0x80 + atom.Domain);
        Span<byte> payload = stackalloc byte[16];
        int payloadLength;
        if (atom.HasDigest)
        {
            atom.Digest.WriteBytes(payload);
            payloadLength = 16;
        }
        else
        {
            payload[0] = (byte)atom.Value;
            payload[1] = (byte)(atom.Value >> 8);
            payloadLength = 2;
        }

        int n = 1;
        for (int i = 0; i < payloadLength; i++)
        {
            output[n++] = (byte)(0xA0 | (payload[i] >> 4));
            output[n++] = (byte)(0xB0 | (payload[i] & 0x0F));
        }
        return n;
    }

    private static int PieceOrdinal(Piece piece) => piece switch
    {
        Piece.WPawn => 0, Piece.WKnight => 1, Piece.WBishop => 2,
        Piece.WRook => 3, Piece.WQueen => 4, Piece.WKing => 5,
        Piece.BPawn => 6, Piece.BKnight => 7, Piece.BBishop => 8,
        Piece.BRook => 9, Piece.BQueen => 10, Piece.BKing => 11,
        _ => throw new ArgumentOutOfRangeException(nameof(piece), "empty is not a piece-square atom"),
    };

    /// <summary>
    /// Usually absent. If a snapshot has zero or multiple candidate rooks on a retained
    /// castling flank, preserve the explicitly designated rook file so two different legal
    /// futures cannot collide in the transition floor. Low byte is White files, high Black.
    /// </summary>
    private static ushort? AmbiguousCastlingRookOverride(Board board)
    {
        ushort packed = 0;
        bool needed = false;
        Check(white: true, kingSide: true, CastleRights.WhiteKing, board.WhiteKingRookFile);
        Check(white: true, kingSide: false, CastleRights.WhiteQueen, board.WhiteQueenRookFile);
        Check(white: false, kingSide: true, CastleRights.BlackKing, board.BlackKingRookFile);
        Check(white: false, kingSide: false, CastleRights.BlackQueen, board.BlackQueenRookFile);
        return needed ? packed : null;

        void Check(bool white, bool kingSide, CastleRights right, int designatedFile)
        {
            if ((board.Castle & right) == 0) return;
            int kingSquare = board.FindKing(white);
            if (kingSquare < 0) { Add(white, designatedFile); return; }
            int rank = Board.RankOf(kingSquare), kingFile = Board.FileOf(kingSquare);
            Piece rook = white ? Piece.WRook : Piece.BRook;
            int count = 0, onlyFile = -1;
            for (int file = 0; file < 8; file++)
            {
                if ((kingSide ? file <= kingFile : file >= kingFile)
                    || board.Squares[Board.Sq(file, rank)] != rook) continue;
                count++;
                onlyFile = file;
            }
            if (count != 1 || onlyFile != designatedFile) Add(white, designatedFile);
        }

        void Add(bool white, int file)
        {
            needed = true;
            int shift = white ? 0 : 8;
            packed |= (ushort)(1 << (shift + (file & 7)));
        }
    }
}
