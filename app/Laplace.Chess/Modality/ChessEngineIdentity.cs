using Laplace.Engine.Core;

namespace Laplace.Modality.Chess;

/// <summary>
/// Chess identity as content, byte-identical to the Engine's (Laplace-Engine <c>src/chess.c</c>, the header
/// comment and <c>id_position</c> / <c>id_move</c>): a codepoint's id is BLAKE3 of its UTF-8 bytes, a
/// composition's id is Native's <c>lp_id_compose</c> over its children's ids in order, a one-child composition
/// is its child, and nothing else enters a hash (no domain, no tier, no rules, no counters).
///
///   square    [file, rank]                         e4 is [e, 4], two codepoints
///   piece     a codepoint, FEN's letter            K Q R B N P white, k q r b n p black
///   board     [[piece, square], ...]               the pieces in square order a1 b1 ... h8
///   castling  [White's mask, Black's mask]         each mask a decimal number's digits; bit 7 - f for file f,
///                                                  the king's file (its centre bit) and the file of each rook
///                                                  it may castle with; a side that cannot castle is 0
///   position  [board, side, castling, en passant]  side w or b; the en passant square only where a legal
///                                                  capture of it exists, else -
///   move      [piece, from, to(, promoted)]        a castling is [king, king's square, its rook's square]
///   transition [position, move, position]          the claim a played game attests, scored by the result
///
/// <see cref="Hash128.Merkle"/> and <see cref="Hash128.Blake3"/> are the monorepo core's binding of those two
/// Native functions (NativeIdentityParityTests pins them to <c>lp_id_compose</c> / <c>lp_id_codepoint</c>).
/// Only the rules the Engine reads (Standard, which Chess960 plays under) have this identity: a rule set with
/// drops, check counts or another objective has none, and <see cref="TryPositionId"/> says so.
/// </summary>
public static class ChessEngineIdentity
{
    // Any tier: Native's compose ignores it, and the id is the same at every one.
    private const byte Tier = 1;

    private static readonly Hash128[] Ascii = BuildAscii();

    private static Hash128[] BuildAscii()
    {
        var table = new Hash128[128];
        Span<byte> one = stackalloc byte[1];
        for (int c = 0; c < table.Length; c++)
        {
            one[0] = (byte)c;
            table[c] = Hash128.Blake3(one);
        }
        return table;
    }

    /// <summary>A codepoint's id: BLAKE3 of its UTF-8 bytes (ASCII here).</summary>
    public static Hash128 Codepoint(char c) => Ascii[c];

    /// <summary>Native <c>lp_id_compose</c>: one child is itself, otherwise BLAKE3-128 over the children's ids.</summary>
    public static Hash128 Compose(ReadOnlySpan<Hash128> children)
    {
        if (children.IsEmpty) throw new ArgumentException("a composition has children", nameof(children));
        return children.Length == 1 ? children[0] : Hash128.Merkle(Tier, children);
    }

    public static Hash128 Square(int file, int rank)
    {
        Span<Hash128> k = stackalloc Hash128[2];
        k[0] = Codepoint((char)('a' + file));
        k[1] = Codepoint((char)('1' + rank));
        return Compose(k);
    }

    /// <summary>A number as its decimal digits (137 is [1, 3, 7]); a single digit is its codepoint.</summary>
    public static Hash128 Number(uint value)
    {
        Span<char> digits = stackalloc char[10];
        int n = 0;
        foreach (char d in value.ToString(System.Globalization.CultureInfo.InvariantCulture)) digits[n++] = d;
        Span<Hash128> k = stackalloc Hash128[n];
        for (int i = 0; i < n; i++) k[i] = Codepoint(digits[i]);
        return Compose(k);
    }

    /// <summary>
    /// The Engine's castling mask for one side: 0 when it may not castle, else the king's file bit plus the
    /// file bit of each rook it may still castle with. File f is bit 7 - f, so the binary reads a to h.
    /// </summary>
    public static uint CastleMask(Board board, bool white)
    {
        CastleRights kingSide = white ? CastleRights.WhiteKing : CastleRights.BlackKing;
        CastleRights queenSide = white ? CastleRights.WhiteQueen : CastleRights.BlackQueen;
        bool k = (board.Castle & kingSide) != 0, q = (board.Castle & queenSide) != 0;
        if (!k && !q) return 0;
        uint mask = 0;
        int king = board.FindKing(white);
        if (king >= 0) mask |= 0x80u >> Board.FileOf(king);
        if (k) mask |= 0x80u >> board.CastleRookFile(white, kingSide: true);
        if (q) mask |= 0x80u >> board.CastleRookFile(white, kingSide: false);
        return mask;
    }

    /// <summary>Whether the board's rules are ones the Engine's identity covers (Standard, Chess960 included).</summary>
    public static bool Covers(Board board) => board.Rules.IsStandard;

    public static bool TryPositionId(Board board, out Hash128 id)
    {
        if (!Covers(board)) { id = default; return false; }
        id = PositionId(board);
        return true;
    }

    /// <summary>The position's id, equal to the Engine's for the same board, side, castling and en passant.</summary>
    public static Hash128 PositionId(Board board)
    {
        if (!Covers(board))
            throw new NotSupportedException($"rules '{board.Rules}' have no Engine position identity");

        Span<Hash128> pieces = stackalloc Hash128[64];
        int n = 0;
        Span<Hash128> two = stackalloc Hash128[2];
        for (int rank = 0; rank < 8; rank++)
        {
            for (int file = 0; file < 8; file++)
            {
                Piece p = board.Squares[Board.Sq(file, rank)];
                if (p == Piece.Empty) continue;
                two[0] = Codepoint(Board.PieceToChar(p));
                two[1] = Square(file, rank);
                pieces[n++] = Compose(two);
            }
        }

        Span<Hash128> part = stackalloc Hash128[4];
        part[0] = Compose(pieces[..n]);
        part[1] = Codepoint(board.WhiteToMove ? 'w' : 'b');
        two[0] = Number(CastleMask(board, white: true));
        two[1] = Number(CastleMask(board, white: false));
        part[2] = Compose(two);
        int ep = ChessModality.CapturableEpSquare(board);
        part[3] = ep < 0 ? Codepoint('-') : Square(Board.FileOf(ep), Board.RankOf(ep));
        return Compose(part);
    }

    /// <summary>The move's id, equal to the Engine's: [piece, from, to] or with the promoted piece.</summary>
    public static Hash128 MoveId(Board before, ChessMove move)
    {
        if (move.IsDrop) throw new NotSupportedException("a drop has no Engine move identity");
        bool white = before.WhiteToMove;
        Piece moving = before.Squares[move.From];
        int toSquare = move.To;
        if (move.IsCastle)
        {
            // The king onto its own rook: a Chess960 castling is never a king's step.
            toSquare = Board.Sq(before.CastleRookFile(white, move.IsKingSideCastle), Board.RankOf(move.From));
        }

        Span<Hash128> k = stackalloc Hash128[4];
        k[0] = Codepoint(Board.PieceToChar(moving));
        k[1] = Square(Board.FileOf(move.From), Board.RankOf(move.From));
        k[2] = Square(Board.FileOf(toSquare), Board.RankOf(toSquare));
        int count = 3;
        if (move.IsPromotion)
        {
            char letter = Board.PieceToChar(Board.TypeOf(move.Promotion));
            k[3] = Codepoint(white ? letter : char.ToLowerInvariant(letter));
            count = 4;
        }
        return Compose(k[..count]);
    }

    /// <summary>[position before, move, position after]: the claim a played game attests.</summary>
    public static Hash128 TransitionId(Hash128 before, Hash128 move, Hash128 after)
    {
        Span<Hash128> k = stackalloc Hash128[3];
        k[0] = before; k[1] = move; k[2] = after;
        return Compose(k);
    }

    /// <summary>The trajectory [P0, M0, P1, ..., Pn] of a line; a line of no moves is its start position.</summary>
    public static Hash128 LineId(ReadOnlySpan<Hash128> trajectory) => Compose(trajectory);

    /// <summary>
    /// Replays <paramref name="moves"/> from <paramref name="start"/> and returns the line's trajectory
    /// [P0, M0, P1, ..., Pn] over Engine-identity ids.
    /// </summary>
    public static Hash128[] Trajectory(Board start, IEnumerable<ChessMove> moves)
    {
        var board = start.Clone();
        var ids = new List<Hash128> { PositionId(board) };
        foreach (var move in moves)
        {
            ids.Add(MoveId(board, move));
            MoveApply.Make(board, move);
            ids.Add(PositionId(board));
        }
        return ids.ToArray();
    }
}
