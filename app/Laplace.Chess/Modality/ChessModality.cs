using System.Collections.Immutable;
using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Modality.Chess;

public sealed class ChessModality : ITurnModality<ChessState, ChessMove>
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public string Name => "chess";

    public ChessState Initial() => FromFen(StartFen);

    /// <summary>The position a game under these rules starts from: its law's start as the Engine's firmware declares
    /// it (Horde, Racing Kings), else Standard's array.</summary>
    public ChessState Initial(ChessVariantRules rules)
        => FromFen(rules.IsStandard ? StartFen : NativeChessRules.LawStart(rules.Law) ?? StartFen, rules);

    public ChessState FromFen(string fen, ChessVariantRules? rules = null)
    {
        var board = Board.FromFen(fen, rules);
        return new ChessState(board, ImmutableList.Create(ChessPositionIdentity.PositionId(board)));
    }

    public string StateKey(ChessState state) => CanonicalKey(state.Board);

    private static string CanonicalKey(Board b) => PositionContent.Surface(b, CanonicalEp(b));

    private static string CanonicalEp(Board b)
    {
        int ep = CapturableEpSquare(b);
        return ep < 0 ? "-" : Board.SquareToAlgebraic(ep);
    }

    /// <summary>
    /// The en-passant target square (0x88 index) only when a LEGAL en-passant capture
    /// exists, else -1. Position identity and the Syzygy probe share this ep fact; a
    /// double-push square with no capturer is not part of the position.
    /// </summary>
    public static int CapturableEpSquare(Board b)
    {
        if (b.EpSquare < 0) return -1;
        if (!b.Rules.IsStandard)
            return MoveGen.Legal(b).Any(move => (move.Flags & MoveFlags.EnPassant) != 0)
                ? b.EpSquare : -1;
        bool white = b.WhiteToMove;
        Piece pawn = white ? Piece.WPawn : Piece.BPawn;
        int from1 = white ? b.EpSquare - 17 : b.EpSquare + 17;
        int from2 = white ? b.EpSquare - 15 : b.EpSquare + 15;
        foreach (int from in new[] { from1, from2 })
        {
            if (!Board.OnBoard(from) || b.Squares[from] != pawn) continue;
            var nb = b.Clone();
            MoveApply.Make(nb, new ChessMove(from, b.EpSquare, Piece.Empty, MoveFlags.EnPassant));
            if (!MoveGen.InCheck(nb, white))
                return b.EpSquare;
        }
        return -1;
    }

    public string ActionKey(ChessState state, ChessMove action) => action.ToUci();

    public IReadOnlyList<ChessMove> LegalActions(ChessState state)
    {
        var moves = MoveGen.Legal(state.Board);
        return Terminal(state, moves) is not null ? Array.Empty<ChessMove>() : moves;
    }

    public ChessState Apply(ChessState state, ChessMove action)
    {
        var nb = state.Board.Clone();
        Piece moving = nb.MovingPiece(action);
        bool isPawn = Board.TypeOf(moving) == Piece.WPawn;
        bool isCapture = nb.Squares[action.To] != Piece.Empty || (action.Flags & MoveFlags.EnPassant) != 0;

        MoveApply.Make(nb, action);

        Hash128 key = ChessPositionIdentity.PositionId(nb);
        var history = !nb.Rules.Drops && (isPawn || isCapture)
            ? ImmutableList.Create(key)
            : state.RepetitionHistory.Add(key);

        return new ChessState(nb, history);
    }

    public int SideToMove(ChessState state) => state.Board.WhiteToMove ? 0 : 1;

    public GameOutcome? Terminal(ChessState state)
        => Terminal(state, MoveGen.Legal(state.Board));

    private static GameOutcome? Terminal(ChessState state, IReadOnlyList<ChessMove> moves)
    {
        var b = state.Board;
        if (RuleWinner(b) is { } variantWinner) return GameOutcome.WonBy(variantWinner);
        if (moves.Count == 0)
        {
            if (MoveGen.InCheck(b, b.WhiteToMove))
            {
                int winner = b.WhiteToMove ? 1 : 0;
                return GameOutcome.WonBy(winner);
            }
            return GameOutcome.Draw;
        }
        // A mating move ends the game before a simultaneous draw counter or
        // repetition claim can supersede its result.
        if ((!b.Rules.Drops && b.HalfmoveClock >= 100) || IsThreefold(state) || IsInsufficientMaterial(b))
            return GameOutcome.Draw;
        return null;
    }

    /// <summary>The side a law other than Standard's says has won here (0 White, 1 Black), by the Engine's judgment of
    /// the position (<see cref="NativeChessRules.Result"/>: the hill, the checks, an exploded king, Antichess's side
    /// with no move, mate); null under Standard's law or while play goes on. A draw by the law is left to the caller's
    /// no-move handling.</summary>
    internal static int? RuleWinner(Board b)
        => b.Rules.IsStandard ? null : NativeChessRules.Result(b) is int r and >= 0 ? r : null;

    private static bool IsThreefold(ChessState state)
    {
        if (state.RepetitionHistory.Count == 0) return false;
        Hash128 current = state.RepetitionHistory[^1];
        int count = 0;
        foreach (var k in state.RepetitionHistory)
            if (k == current) count++;
        return count >= 3;
    }

    internal static bool IsInsufficientMaterial(Board b)
    {
        // Reserve material and recyclable minor pieces invalidate standard-chess
        // dead-position conclusions. Bare kings with empty reserves remain dead.
        if (b.Rules.WinBySquares.Length > 0) return false;
        if (b.Rules.Drops || b.Rules.WinByCheckCount > 0)
            return b.Pockets.All(p => p == 0) && (b.OccupiedBB == (b.PieceBB(Piece.WKing) | b.PieceBB(Piece.BKing)));
        int whiteKnights = 0, whiteBishops = 0, blackKnights = 0, blackBishops = 0;
        bool whiteBishopOnLight = false, whiteBishopOnDark = false;
        bool blackBishopOnLight = false, blackBishopOnDark = false;

        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = b.Squares[sq];
            if (p == Piece.Empty) continue;
            switch (Board.TypeOf(p))
            {
                case Piece.WPawn:
                case Piece.WRook:
                case Piece.WQueen:
                    return false;
                case Piece.WKnight:
                    if (Board.IsWhite(p)) whiteKnights++; else blackKnights++;
                    break;
                case Piece.WBishop:
                    bool light = ((Board.FileOf(sq) + Board.RankOf(sq)) & 1) == 1;
                    if (Board.IsWhite(p)) { whiteBishops++; if (light) whiteBishopOnLight = true; else whiteBishopOnDark = true; }
                    else { blackBishops++; if (light) blackBishopOnLight = true; else blackBishopOnDark = true; }
                    break;
                case Piece.WKing:
                    break;
            }
        }

        int whiteMinors = whiteKnights + whiteBishops;
        int blackMinors = blackKnights + blackBishops;

        if (whiteMinors == 0 && blackMinors == 0) return true;
        if (whiteMinors == 1 && blackMinors == 0) return true;
        if (blackMinors == 1 && whiteMinors == 0) return true;
        if (whiteKnights == 0 && blackKnights == 0 && whiteBishops + blackBishops > 0)
        {
            bool anyLight = whiteBishopOnLight || blackBishopOnLight;
            bool anyDark = whiteBishopOnDark || blackBishopOnDark;
            if (!(anyLight && anyDark)) return true;
        }
        return false;
    }
}
