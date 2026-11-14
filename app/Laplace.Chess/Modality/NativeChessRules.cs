using System.Runtime.InteropServices;
using System.Text;

namespace Laplace.Modality.Chess;

// Native rule execution is separate from observed game testimony. The rules are the Engine's one implementation of
// chess's laws (Laplace-Engine src/chess_rules.c, built into laplace_core), each law a variant its
// firmware/chess.firmware declares; this class only carries the managed board across.
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
        public fixed uint Checks[2];
        public fixed byte Law[64];
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
    [DllImport("laplace_core", EntryPoint = "laplace_chess_result", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ResultNative(State* state);
    [DllImport("laplace_core", EntryPoint = "laplace_chess_laws", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LawsNative(byte* path);
    [DllImport("laplace_core", EntryPoint = "laplace_chess_law_known", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LawKnownNative(byte* name);
    [DllImport("laplace_core", EntryPoint = "laplace_chess_law_start", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LawStartNative(byte* name, byte* output, nuint capacity);
    [DllImport("laplace_core", EntryPoint = "laplace_chess_law_canonical", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LawCanonicalNative(byte* name, byte* output, nuint capacity);
    [DllImport("laplace_core", EntryPoint = "laplace_chess_law_name", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LawNameNative(int index, byte* output, nuint capacity);

    /// <summary>The law's own name for a name it goes by (a PGN tag, Lichess's API key, a UCI_Variant value), as the
    /// Engine's firmware declares it; null when no law has that name.</summary>
    internal static string? LawCanonical(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        _ = Laws.Value;
        byte[] bytes = Encoding.UTF8.GetBytes(name + "\0"); Span<byte> output = stackalloc byte[64];
        int n;
        fixed (byte* p = bytes) fixed (byte* o = output) n = LawCanonicalNative(p, o, (nuint)output.Length);
        return n < 0 ? null : Encoding.UTF8.GetString(output[..n]);
    }

    /// <summary>Every name a law goes by, in the firmware's order.</summary>
    internal static IReadOnlyList<string> LawNames()
    {
        _ = Laws.Value;
        var names = new List<string>(); Span<byte> output = stackalloc byte[64];
        for (int i = 0; ; i++)
        {
            int n;
            fixed (byte* o = output) n = LawNameNative(i, o, (nuint)output.Length);
            if (n < 0) return names;
            names.Add(Encoding.UTF8.GetString(output[..n]));
        }
    }

    /// <summary>The Engine's chess firmware, beside the program firmware of the recipe estate this host runs
    /// (LAPLACE_CHESS_FIRMWARE names another); read once per process.</summary>
    private static readonly Lazy<int> Laws = new(() =>
    {
        var configured = Environment.GetEnvironmentVariable("LAPLACE_CHESS_FIRMWARE");
        var path = !string.IsNullOrWhiteSpace(configured) ? configured
            : Path.Combine(Directory.GetParent(Laplace.Decomposers.Abstractions.EtlManifest.RecipesRoot)!.FullName, "firmware", "chess.firmware");
        byte[] bytes = Encoding.UTF8.GetBytes(path + '\0');
        fixed (byte* p = bytes) return LawsNative(p);
    });

    /// <summary>Whether the Engine's firmware declares a law of this name, as a PGN Variant tag or Lichess writes it.</summary>
    internal static bool LawKnown(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        _ = Laws.Value;
        byte[] bytes = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* p = bytes) return LawKnownNative(p) != 0;
    }

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
        s.Checks[0] = board.Checks[0]; s.Checks[1] = board.Checks[1];
        var law = Encoding.UTF8.GetBytes(board.Rules.Law);
        if (law.Length >= 64) throw new NotSupportedException($"Law name too long: {board.Rules.Law}");
        for (int i = 0; i < law.Length; i++) s.Law[i] = law[i];
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

    /// <summary>The FEN the law starts from, as the Engine's firmware declares it; null when it starts from
    /// Standard's array.</summary>
    internal static string? LawStart(string name)
    {
        if (!LawKnown(name)) throw new InvalidDataException($"The Engine's chess firmware declares no law {name}.");
        byte[] bytes = Encoding.UTF8.GetBytes(name + '\0'); Span<byte> fen = stackalloc byte[128];
        int n;
        fixed (byte* p = bytes) fixed (byte* f = fen) n = LawStartNative(p, f, (nuint)fen.Length);
        if (n < 0) throw new InvalidDataException($"The start of law {name} does not fit.");
        return n == 0 ? null : Encoding.UTF8.GetString(fen[..n]);
    }

    internal static bool Supports(ChessVariantRules rules) => rules.IsStandard || LawKnown(rules.Law);

    /// <summary>How the board stands by its law alone: null while play goes on, else the winner (0 White, 1 Black) or a
    /// draw (-1). The clocks, repetition and material are the caller's.</summary>
    internal static int? Result(Board board)
    {
        var s = Pack(board);
        return ResultNative(&s) switch
        {
            0 => null, 1 => 0, 2 => 1, 3 => -1,
            _ => throw new InvalidDataException($"The Engine's chess firmware declares no law {board.Rules.Law}."),
        };
    }

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
