using System.Runtime.InteropServices;
using System.Text;

namespace Laplace.Modality.Chess;

// Native rule execution is separate from observed game testimony. The board and
// move values returned here are composed by the same substrate owners as chess.
internal static unsafe class NativeChessRules
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public fixed sbyte Squares[128];
        public fixed ushort Pockets[10];
        public ulong Promoted;
        public int White, Castle, Ep, Halfmove, Fullmove;
        public fixed int RookFiles[4];
        public int Drops;
        public fixed uint Checks[2];
        public uint CheckTarget;
        public ulong WinningSquares;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Move
    {
        public int From, To, Promotion, Flags;
        public readonly ChessMove Managed(bool white) => new(From, To,
            (Piece)(((Flags & (int)MoveFlags.Promotion) != 0 && !white) ? -Promotion : Promotion), (MoveFlags)Flags);
    }

    [DllImport("laplace_core", EntryPoint = "laplace_chess_moves", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint MovesNative(State* state, int legal, Move* moves, nuint capacity);
    [DllImport("laplace_core", EntryPoint = "laplace_chess_apply", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ApplyNative(State* state, Move* move);
    [DllImport("laplace_core", EntryPoint = "laplace_chess_resolve", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ResolveNative(State* state, byte* san, Move* move);
    [DllImport("laplace_core", EntryPoint = "laplace_chess_replay", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReplayNative(State* initial, byte* sans, nuint bytes, nuint count,
        State* states, Move* moves, nuint* failedPly);

    private static State Pack(Board board)
    {
        if (!Supports(board.Rules)) throw new NotSupportedException($"Native chess execution does not yet implement {board.Rules}.");
        State s = default;
        for (int i = 0; i < 128; i++) s.Squares[i] = (sbyte)board.Squares[i];
        for (int i = 0; i < 10; i++) s.Pockets[i] = board.Pockets[i];
        s.Promoted = board.Promoted;
        s.White = board.WhiteToMove ? 1 : 0; s.Castle = (int)board.Castle;
        s.Ep = board.EpSquare; s.Halfmove = board.HalfmoveClock; s.Fullmove = board.FullmoveNumber;
        s.RookFiles[0] = board.WhiteKingRookFile; s.RookFiles[1] = board.WhiteQueenRookFile;
        s.RookFiles[2] = board.BlackKingRookFile; s.RookFiles[3] = board.BlackQueenRookFile;
        s.Drops = board.Rules.Drops ? 1 : 0;
        s.Checks[0] = board.Checks[0]; s.Checks[1] = board.Checks[1];
        s.CheckTarget = (uint)board.Rules.WinByCheckCount;
        foreach (var square in board.Rules.WinBySquares.Split(',', StringSplitOptions.RemoveEmptyEntries))
            s.WinningSquares |= 1UL << Bitboards.Bit(Board.AlgebraicToSquare(square));
        return s;
    }

    private static void Unpack(State s, Board board)
    {
        for (int i = 0; i < 128; i++) if (Board.OnBoard(i)) board.Set(i, (Piece)s.Squares[i]);
        for (int i = 0; i < 10; i++) board.Pockets[i] = s.Pockets[i];
        board.Promoted = s.Promoted;
        board.Checks[0] = s.Checks[0]; board.Checks[1] = s.Checks[1];
        board.WhiteToMove = s.White != 0; board.Castle = (CastleRights)s.Castle;
        board.EpSquare = s.Ep; board.HalfmoveClock = s.Halfmove; board.FullmoveNumber = s.Fullmove;
        board.WhiteKingRookFile = (sbyte)s.RookFiles[0]; board.WhiteQueenRookFile = (sbyte)s.RookFiles[1];
        board.BlackKingRookFile = (sbyte)s.RookFiles[2]; board.BlackQueenRookFile = (sbyte)s.RookFiles[3];
    }

    internal static bool Supports(ChessVariantRules rules) => rules.IsStandard
        || rules == ChessVariants.Crazyhouse || rules == ChessVariants.ThreeCheck || rules == ChessVariants.KingOfTheHill;

    internal static void Moves(Board board, List<ChessMove> output, bool legal, int? from = null)
    {
        var s = Pack(board);
        int count = checked((int)MovesNative(&s, legal ? 1 : 0, null, 0));
        Span<Move> moves = count <= 256 ? stackalloc Move[count] : new Move[count];
        fixed (Move* ptr = moves)
            if (MovesNative(&s, legal ? 1 : 0, ptr, (nuint)count) != (nuint)count)
                throw new InvalidOperationException("Native chess move enumeration changed its required capacity.");
        output.Clear();
        foreach (var m in moves) if (from is null || m.From == from) output.Add(m.Managed(board.WhiteToMove));
    }

    internal static ChessMove? Resolve(Board board, string san)
    {
        var s = Pack(board);
        byte[] bytes = Encoding.UTF8.GetBytes(san + '\0');
        Move move;
        fixed (byte* p = bytes)
            return ResolveNative(&s, p, &move) == 0 ? move.Managed(board.WhiteToMove) : null;
    }

    internal static void Apply(Board board, ChessMove move)
    {
        var s = Pack(board);
        var m = new Move { From = move.From, To = move.To, Promotion = Math.Abs((int)move.Promotion), Flags = (int)move.Flags };
        if (ApplyNative(&s, &m) != 0) throw new InvalidDataException($"Illegal move {move} for the declared chess rules.");
        Unpack(s, board);
    }

    internal static (Board[] Boards, ChessMove[] Moves) Replay(Board initial, IReadOnlyList<string> sans)
    {
        var s = Pack(initial);
        byte[] text = Encoding.UTF8.GetBytes(sans.Count == 0 ? "" : string.Join('\0', sans) + '\0');
        var states = new State[sans.Count + 1]; var moves = new Move[sans.Count];
        nuint failed;
        fixed (byte* p = text)
        fixed (State* output = states)
        fixed (Move* actions = moves)
        {
            // Empty arrays may pin to null; the native zero-ply contract still
            // receives a valid byte buffer and returns the initial position.
            byte empty = 0;
            int rc = ReplayNative(&s, p == null ? &empty : p, (nuint)text.Length,
                (nuint)sans.Count, output, actions, &failed);
            if (rc != 0) throw new InvalidDataException(
                $"illegal move at ply {failed + 1}: '{(failed < (nuint)sans.Count ? sans[(int)failed] : "<end>")}' under {initial.Rules}");
        }
        var boards = new Board[states.Length]; var resolved = new ChessMove[moves.Length];
        for (int i = 0; i < states.Length; i++) { boards[i] = new Board { Rules = initial.Rules }; Unpack(states[i], boards[i]); }
        for (int i = 0; i < moves.Length; i++) resolved[i] = moves[i].Managed(boards[i].WhiteToMove);
        return (boards, resolved);
    }
}
