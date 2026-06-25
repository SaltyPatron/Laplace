using System.Collections.Immutable;
using System.Text;

namespace Laplace.Modality.Chess;

/// <summary>
/// The <see cref="ITurnModality{TState,TAction}"/> instance for standard chess. Pure C#, no DB.
/// Movegen correctness is gated by perft; see the test project.
/// </summary>
public sealed class ChessModality : ITurnModality<ChessState, ChessMove>
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    public string Name => "chess";

    public ChessState Initial() => FromFen(StartFen);

    /// <summary>Build a state from a full FEN, seeding the repetition history with the start position key.</summary>
    public ChessState FromFen(string fen)
    {
        var board = Board.FromFen(fen);
        return new ChessState(board, ImmutableList.Create(CanonicalKey(board)));
    }

    /// <summary>
    /// Canonical content key: the first FOUR FEN fields (placement, side, castling, en-passant),
    /// OMITTING the halfmove clock and fullmove number, so transpositions collapse. The en-passant
    /// field is canonicalized to "-" unless an en-passant capture is actually available to the side
    /// to move (matching Lichess / standard canonicalization).
    /// </summary>
    public string StateKey(ChessState state) => CanonicalKey(state.Board);

    // The position's canonical content key IS its substructure decomposition (PositionContent.Surface):
    // the position composes from real chess substructures (piece-square, pawn-skeleton, pawn-features,
    // material), so identical substructures across positions share one content node and the S³ geometry
    // is real. Faithful + transposition-stable (counters/history excluded); en-passant canonicalized to
    // a live capture only.
    private static string CanonicalKey(Board b) => PositionContent.Surface(b, CanonicalEp(b));

    /// <summary>En-passant target only if a pawn of the side to move can actually capture there.</summary>
    private static string CanonicalEp(Board b)
    {
        if (b.EpSquare < 0) return "-";
        bool white = b.WhiteToMove;
        Piece pawn = white ? Piece.WPawn : Piece.BPawn;
        // A capturing pawn would sit one rank below (white) / above (black) the ep square, adjacent file.
        int from1 = white ? b.EpSquare - 17 : b.EpSquare + 17;
        int from2 = white ? b.EpSquare - 15 : b.EpSquare + 15;
        foreach (int from in new[] { from1, from2 })
        {
            if (!Board.OnBoard(from) || b.Squares[from] != pawn) continue;
            // The capture must be legal (king not left in check).
            var nb = b.Clone();
            MoveApply.Make(nb, new ChessMove(from, b.EpSquare, Piece.Empty, MoveFlags.EnPassant));
            if (!MoveGen.InCheck(nb, white))
                return Board.SquareToAlgebraic(b.EpSquare);
        }
        return "-";
    }

    public string ActionKey(ChessState state, ChessMove action) => action.ToUci();

    public IReadOnlyList<ChessMove> LegalActions(ChessState state)
    {
        if (Terminal(state) is not null) return Array.Empty<ChessMove>();
        return MoveGen.Legal(state.Board);
    }

    public ChessState Apply(ChessState state, ChessMove action)
    {
        var nb = state.Board.Clone();
        Piece moving = nb.Squares[action.From];
        bool isPawn = Board.TypeOf(moving) == Piece.WPawn;
        bool isCapture = nb.Squares[action.To] != Piece.Empty || (action.Flags & MoveFlags.EnPassant) != 0;

        MoveApply.Make(nb, action);

        string key = CanonicalKey(nb);
        // Irreversible move (pawn move or capture) resets the repetition window.
        var history = (isPawn || isCapture)
            ? ImmutableList.Create(key)
            : state.RepetitionHistory.Add(key);

        return new ChessState(nb, history);
    }

    public int SideToMove(ChessState state) => state.Board.WhiteToMove ? 0 : 1;

    public GameOutcome? Terminal(ChessState state)
    {
        var b = state.Board;

        // 50-move rule: halfmove clock >= 100.
        if (b.HalfmoveClock >= 100) return GameOutcome.Draw;

        // Threefold repetition: this 4-field key has appeared >= 3 times in the window.
        if (IsThreefold(state)) return GameOutcome.Draw;

        // Insufficient material.
        if (IsInsufficientMaterial(b)) return GameOutcome.Draw;

        var moves = MoveGen.Legal(b);
        if (moves.Count > 0) return null; // game continues

        // No legal moves: checkmate or stalemate.
        bool inCheck = MoveGen.InCheck(b, b.WhiteToMove);
        if (inCheck)
        {
            // Side to move is checkmated; the other side won.
            int winner = b.WhiteToMove ? 1 : 0; // black wins if white to move is mated
            return GameOutcome.WonBy(winner);
        }
        return GameOutcome.Draw; // stalemate
    }

    private static bool IsThreefold(ChessState state)
    {
        if (state.RepetitionHistory.Count == 0) return false;
        string current = state.RepetitionHistory[^1];
        int count = 0;
        foreach (var k in state.RepetitionHistory)
            if (k == current) count++;
        return count >= 3;
    }

    private static bool IsInsufficientMaterial(Board b)
    {
        // Count minor pieces; any pawn, rook, or queen => sufficient.
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

        // K vs K
        if (whiteMinors == 0 && blackMinors == 0) return true;
        // K+minor vs K (single bishop or knight)
        if (whiteMinors == 1 && blackMinors == 0) return true;
        if (blackMinors == 1 && whiteMinors == 0) return true;
        // K+B vs K+B with both bishops on same colour
        if (whiteKnights == 0 && blackKnights == 0 && whiteBishops >= 1 && blackBishops >= 1)
        {
            bool anyLight = whiteBishopOnLight || blackBishopOnLight;
            bool anyDark = whiteBishopOnDark || blackBishopOnDark;
            if (!(anyLight && anyDark)) return true; // all bishops same colour
        }
        return false;
    }
}
