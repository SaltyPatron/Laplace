using System.Collections.Immutable;
using System.Text;
using Laplace.Engine.Core;

namespace Laplace.Modality.Chess;

public sealed class ChessModality : ITurnModality<ChessState, ChessMove>
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public string Name => "chess";

    public ChessState Initial() => FromFen(StartFen);

    public ChessState FromFen(string fen)
    {
        var board = Board.FromFen(fen);
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
    /// exists, else -1 — the canonical ep fact position identity and the syzygy probe
    /// share (a raw double-push square with no capturer is not part of the position).
    /// </summary>
    public static int CapturableEpSquare(Board b)
    {
        if (b.EpSquare < 0) return -1;
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
        Piece moving = nb.Squares[action.From];
        bool isPawn = Board.TypeOf(moving) == Piece.WPawn;
        bool isCapture = nb.Squares[action.To] != Piece.Empty || (action.Flags & MoveFlags.EnPassant) != 0;

        MoveApply.Make(nb, action);

        Hash128 key = ChessPositionIdentity.PositionId(nb);
        var history = (isPawn || isCapture)
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
        if (b.HalfmoveClock >= 100 || IsThreefold(state) || IsInsufficientMaterial(b))
            return GameOutcome.Draw;
        return null;
    }

    private static bool IsThreefold(ChessState state)
    {
        if (state.RepetitionHistory.Count == 0) return false;
        Hash128 current = state.RepetitionHistory[^1];
        int count = 0;
        foreach (var k in state.RepetitionHistory)
            if (k == current) count++;
        return count >= 3;
    }

    private static bool IsInsufficientMaterial(Board b)
    {
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
