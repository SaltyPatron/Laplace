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

    /// <summary>
    /// Header atoms: rules, side to move, castling mask, ambiguous-rook override, en passant.
    /// Every one of these is conditional except side/castling/en-passant; five is the ceiling.
    /// </summary>
    internal const int MaxHeaderAtoms = 5;

    /// <summary>
    /// Upper bound on the atoms <see cref="FillAtoms"/> can emit: the header plus ONE atom per
    /// OCCUPIED SQUARE. The bound is 64 because the board has 64 squares -- NOT 32, because
    /// "32 pieces" is a rule of legal chess and this function hashes whatever board it is
    /// handed. Both call sites previously stackalloc'd a bare 40, which is 5 + 35: enough for
    /// any legal position and nothing else.
    ///
    /// Chess.com "Odds Chess" ships FENs like
    ///   rnbqkbnr/pppppppp/8/8/PPPPPPPP/PPPPPPPP/PPPPPPPP/4K3 w kq - 0 1
    /// -- 41 occupied squares. FillAtoms wrote atom 41 into a 40-slot span and threw
    /// IndexOutOfRangeException out of PositionId, through ChessModality.FromFen, through
    /// TryParseGame, which does not catch it. The whole FILE died, not the game:
    /// Firouzja2003_chesscom.pgn and Hikaru_chesscom.pgn each carry exactly one such game
    /// (2 of 37,099 scanned) and each failed its ingest unit -- seed runs 32438771887 and
    /// 32439795126, "2 unit(s) failed to apply".
    /// </summary>
    internal const int MaxAtoms = MaxHeaderAtoms + 64;

    internal const byte SideDomain = 1;
    internal const byte CastlingDomain = 2;
    internal const byte EnPassantDomain = 3;
    internal const byte PieceSquareDomain = 4;
    internal const byte RulesDomain = 5;
    internal const byte CastlingRookOverrideDomain = 6;
    internal const byte MovePieceDomain = 16;
    internal const byte MoveFromDomain = 17;
    internal const byte MoveToDomain = 18;
    internal const byte MoveFlagsDomain = 19;
    internal const byte MovePromotionDomain = 20;
    internal const byte AnnotationMissingDomain = 32;

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
        Span<Atom> atoms = stackalloc Atom[MaxAtoms];
        int n = FillAtoms(board, rules ?? ChessVariantRules.Standard, atoms);
        Span<Hash128> ids = stackalloc Hash128[n];
        for (int i = 0; i < n; i++) ids[i] = AtomId(atoms[i]);
        return Hash128.Merkle(PositionTier, ids);
    }

    internal static int FillMoveAtoms(Piece moving, ChessMove move, Span<Atom> atoms)
    {
        atoms[0] = Atom.Scalar(MovePieceDomain, checked((ushort)PieceOrdinal(moving)));
        atoms[1] = Atom.Scalar(MoveFromDomain, checked((ushort)BitIndex(move.From)));
        atoms[2] = Atom.Scalar(MoveToDomain, checked((ushort)BitIndex(move.To)));
        atoms[3] = Atom.Scalar(MoveFlagsDomain, (ushort)move.Flags);
        atoms[4] = Atom.Scalar(MovePromotionDomain, PromotionOrdinal(move.Promotion));
        return 5;
    }

    internal static Hash128 MoveId(Piece moving, ChessMove move)
    {
        Span<Atom> atoms = stackalloc Atom[5];
        FillMoveAtoms(moving, move, atoms);
        Span<Hash128> ids = stackalloc Hash128[5];
        for (int i = 0; i < ids.Length; i++) ids[i] = AtomId(atoms[i]);
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

    /// <summary>
    /// Reverse map for the five MOVE atom domains: atom id -> (domain, value).
    ///
    /// A move id is not opaque. FillMoveAtoms composes it from piece, from-square,
    /// to-square, flags and promotion, and each domain's value space is tiny -- 12
    /// pieces, 64 squares, a handful of flags -- so the whole table is ~160 entries
    /// built once. That is what makes a stored move decodable without a board:
    /// ChessReplay resolves an id by generating every legal action and hashing each
    /// one (~35 hashes per ply) purely because it treats the id as opaque. A fold
    /// over recorded games does not need to search for a move that already happened.
    ///
    /// VERIFIED 2026-08-21 against a stored corpus move: fe6ea447... decodes to
    /// WPawn, e2, e4, DoublePush, no promotion -- 1.e4, all five atoms exact.
    /// </summary>
    public static IReadOnlyDictionary<Hash128, (byte Domain, ushort Value)> MoveAtomIndex =>
        MoveAtomIndexLazy.Value;

    private static readonly Lazy<IReadOnlyDictionary<Hash128, (byte, ushort)>> MoveAtomIndexLazy =
        new(BuildMoveAtomIndex, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static IReadOnlyDictionary<Hash128, (byte, ushort)> BuildMoveAtomIndex()
    {
        var map = new Dictionary<Hash128, (byte, ushort)>(256);
        void Add(byte domain, int maxExclusive)
        {
            for (ushort v = 0; v < maxExclusive; v++)
                map[AtomId(Atom.Scalar(domain, v))] = (domain, v);
        }
        Add(MovePieceDomain, 12);      // PieceOrdinal, white 0-5 then black 6-11
        Add(MoveFromDomain, 64);       // BitIndex: (rank << 3) | file
        Add(MoveToDomain, 64);
        Add(MoveFlagsDomain, 16);      // MoveFlags is a 4-bit set
        Add(MovePromotionDomain, 16);  // PromotionOrdinal, sentinel included
        return map;
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
    /// Reconstruct a board from the two-level typed position trajectory
    /// (position→atoms→byte-atoms). This is the inverse of <see cref="FillAtoms"/>;
    /// FEN is generated only after reconstruction when an interchange surface is requested.
    /// </summary>
    public static bool TryBoardFromAtomConstituents(
        IReadOnlyList<IReadOnlyList<Hash128>> atomConstituents, out Board board)
    {
        board = new Board { EpSquare = -1, FullmoveNumber = 1 };
        var atoms = new List<Atom>(atomConstituents.Count);
        foreach (var ids in atomConstituents)
        {
            if (!TryDecodeAtom(ids, out var atom)) return false;
            atoms.Add(atom);
        }

        ushort? rookOverride = null;
        foreach (var atom in atoms)
        {
            switch (atom.Domain)
            {
                case SideDomain:
                    if (atom.Value > 1) return false;
                    board.WhiteToMove = atom.Value == 1;
                    break;
                case CastlingDomain:
                    if (atom.Value > 15) return false;
                    board.Castle = (CastleRights)atom.Value;
                    break;
                case CastlingRookOverrideDomain:
                    rookOverride = atom.Value;
                    break;
                case EnPassantDomain:
                    if (atom.Value > 64) return false;
                    board.EpSquare = atom.Value == 64
                        ? -1 : Board.Sq(atom.Value & 7, atom.Value >> 3);
                    break;
                case PieceSquareDomain:
                {
                    int pieceOrdinal = atom.Value >> 6;
                    int bit = atom.Value & 63;
                    if (pieceOrdinal > 11) return false;
                    board.Set(Board.Sq(bit & 7, bit >> 3), PieceFromOrdinal(pieceOrdinal));
                    break;
                }
                case RulesDomain:
                    // Rule identity is orthogonal to the board payload. PGN setup replay uses
                    // the board plus its encoded Chess960 rook geometry below.
                    break;
                default:
                    return false;
            }
        }

        if (!ResolveCastlingRookFiles(board, rookOverride)) return false;
        return board.FindKing(white: true) >= 0 && board.FindKing(white: false) >= 0;
    }

    internal static bool TryDecodeAtom(IReadOnlyList<Hash128> ids, out Atom atom)
    {
        atom = default;
        if (ids.Count is not (5 or 33)) return false;
        Span<byte> bytes = stackalloc byte[33];
        for (int i = 0; i < ids.Count; i++)
        {
            if (!TryByteForId(ids[i], out bytes[i])) return false;
        }
        if (bytes[0] < 0x80) return false;
        byte domain = (byte)(bytes[0] - 0x80);
        Span<byte> payload = stackalloc byte[16];
        int payloadLength = (ids.Count - 1) / 2;
        for (int i = 0; i < payloadLength; i++)
        {
            byte hi = bytes[1 + i * 2], lo = bytes[2 + i * 2];
            if ((hi & 0xF0) != 0xA0 || (lo & 0xF0) != 0xB0) return false;
            payload[i] = (byte)(((hi & 0x0F) << 4) | (lo & 0x0F));
        }
        if (payloadLength == 2)
        {
            atom = Atom.Scalar(domain, (ushort)(payload[0] | (payload[1] << 8)));
            return true;
        }
        if (payloadLength == 16 && domain == RulesDomain)
        {
            atom = Atom.Rule(Hash128.FromBytes(payload));
            return true;
        }
        return false;
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

    internal static int PieceOrdinal(Piece piece) => piece switch
    {
        Piece.WPawn => 0, Piece.WKnight => 1, Piece.WBishop => 2,
        Piece.WRook => 3, Piece.WQueen => 4, Piece.WKing => 5,
        Piece.BPawn => 6, Piece.BKnight => 7, Piece.BBishop => 8,
        Piece.BRook => 9, Piece.BQueen => 10, Piece.BKing => 11,
        _ => throw new ArgumentOutOfRangeException(nameof(piece), "empty is not a piece-square atom"),
    };

    private static Piece PieceFromOrdinal(int ordinal) => ordinal switch
    {
        0 => Piece.WPawn, 1 => Piece.WKnight, 2 => Piece.WBishop,
        3 => Piece.WRook, 4 => Piece.WQueen, 5 => Piece.WKing,
        6 => Piece.BPawn, 7 => Piece.BKnight, 8 => Piece.BBishop,
        9 => Piece.BRook, 10 => Piece.BQueen, 11 => Piece.BKing,
        _ => Piece.Empty,
    };

    private static readonly Lazy<Dictionary<Hash128, byte>> ByteById = new(() =>
    {
        var map = new Dictionary<Hash128, byte>(ByteAtoms.Count);
        for (int value = ByteAtoms.First; value <= byte.MaxValue; value++)
            map[ByteAtoms.Id((byte)value)] = (byte)value;
        return map;
    });

    private static bool TryByteForId(Hash128 id, out byte value)
        => ByteById.Value.TryGetValue(id, out value);

    private static bool ResolveCastlingRookFiles(Board board, ushort? packed)
    {
        return Resolve(white: true, kingSide: true, CastleRights.WhiteKing)
            && Resolve(white: true, kingSide: false, CastleRights.WhiteQueen)
            && Resolve(white: false, kingSide: true, CastleRights.BlackKing)
            && Resolve(white: false, kingSide: false, CastleRights.BlackQueen);

        bool Resolve(bool white, bool kingSide, CastleRights right)
        {
            if ((board.Castle & right) == 0) return true;
            int king = board.FindKing(white);
            if (king < 0) return false;
            int kingFile = Board.FileOf(king), rank = white ? 0 : 7;
            Piece rook = white ? Piece.WRook : Piece.BRook;
            int file = -1;
            for (int f = 0; f < 8; f++)
            {
                if ((kingSide ? f <= kingFile : f >= kingFile)) continue;
                if (board.Squares[Board.Sq(f, rank)] != rook) continue;
                if (packed is { } mask
                    && (mask & (1 << ((white ? 0 : 8) + f))) == 0) continue;
                if (file >= 0) return false;
                file = f;
            }
            if (file < 0) return false;
            if (white)
            {
                if (kingSide) board.WhiteKingRookFile = (sbyte)file;
                else board.WhiteQueenRookFile = (sbyte)file;
            }
            else
            {
                if (kingSide) board.BlackKingRookFile = (sbyte)file;
                else board.BlackQueenRookFile = (sbyte)file;
            }
            return true;
        }
    }

    private static int BitIndex(int square)
    {
        if ((square & 0x88) != 0) throw new ArgumentOutOfRangeException(nameof(square));
        return (Board.RankOf(square) << 3) | Board.FileOf(square);
    }

    private static ushort PromotionOrdinal(Piece piece) => Board.TypeOf(piece) switch
    {
        Piece.Empty => 0,
        Piece.WKnight => 1,
        Piece.WBishop => 2,
        Piece.WRook => 3,
        Piece.WQueen => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(piece), "invalid promotion piece"),
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
            int shift = white ? 0 : 8;
            packed |= (ushort)(1 << (shift + (designatedFile & 7)));
            int kingSquare = board.FindKing(white);
            if (kingSquare < 0) { needed = true; return; }
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
            if (count != 1 || onlyFile != designatedFile) needed = true;
        }
    }
}
