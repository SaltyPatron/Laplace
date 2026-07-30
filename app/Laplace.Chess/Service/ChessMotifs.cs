using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public static class ChessMotifs
{
    private static readonly (string Name, string[] Sans)[] NamedTraps =
    [
        ("ScholarsMate", ["e4", "e5", "Bc4", "Nc6", "Qh5", "Nf6", "Qxf7#"]),
        ("ScholarsMate", ["e4", "e5", "Bc4", "Nc6", "Qh5", "Nf6", "Qxf7"]),
        ("FriedLiver", ["e4", "e5", "Nf3", "Nc6", "Bc4", "Nf6", "Ng5", "d5", "exd5", "Nxd5", "Nxf7"]),
    ];

    /// Specific, named, well-known opening traps — matched literally by move sequence, same as
    /// before. Distinct from DetectAtPly below, which finds general tactical shapes from real
    /// board state rather than a fixed list of known sequences.
    public static string? DetectNamedTrap(IReadOnlyList<string> sans)
    {
        foreach (var (name, pattern) in NamedTraps)
        {
            if (sans.Count < pattern.Length) continue;
            bool ok = true;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (!SanMatch(sans[i], pattern[i])) { ok = false; break; }
            }
            if (ok) return name;
        }
        return null;
    }

    private static bool SanMatch(string played, string pattern)
    {
        if (string.Equals(played, pattern, StringComparison.Ordinal)) return true;
        if (pattern.EndsWith('#') && played.StartsWith(pattern[..^1], StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly HashSet<Piece> ForkworthyTargets =
    [
        Piece.WKnight, Piece.BKnight, Piece.WBishop, Piece.BBishop,
        Piece.WRook, Piece.BRook, Piece.WQueen, Piece.BQueen, Piece.WKing, Piece.BKing,
    ];

    /// General tactical shapes detected from real board state at one ply — a fork (the piece
    /// that just moved now attacks 2+ enemy minor-or-greater pieces at once), a discovered check
    /// (the side to move is in check, but not from the piece that just moved — some other
    /// piece's line opened up), and winning material for free (a capture the opponent cannot
    /// immediately recapture). Replaces trying to infer any of this from a SAN string alone.
    public static IEnumerable<string> DetectAtPly(Board before, ChessMove move, Board after)
    {
        bool moverWhite = Board.IsWhite(before.Squares[move.From]);
        var attacked = MoveGen.EnemyPiecesAttackedFrom(after, move.To);

        int valuableHits = attacked.Count(sq => ForkworthyTargets.Contains(after.Squares[sq]));
        if (valuableHits >= 2) yield return "fork";

        if (MoveGen.InCheck(after, whiteKing: !moverWhite) && !attacked.Contains(after.FindKing(!moverWhite)))
            yield return "discovered_check";

        var captured = before.Squares[move.To];
        if (captured != Piece.Empty && !MoveGen.IsSquareAttacked(after, move.To, byWhite: !moverWhite))
            yield return "hanging_piece_won";
    }

    /// <summary>
    /// The fully replayed game handed to the multi-ply detectors: Boards[i] is the
    /// position BEFORE Moves[i] (Boards.Count == Moves.Count + 1). EvalsWhiteCp are the
    /// source's white-POV centipawn evals per ply when annotated (sparse tail allowed;
    /// null when the source carried none). StandardStart says the game began from the
    /// standard array — gambits only mean anything there.
    /// </summary>
    public sealed record ReplayWindow(
        IReadOnlyList<Board> Boards,
        IReadOnlyList<ChessMove> Moves,
        int[]? EvalsWhiteCp,
        bool StandardStart);

    /// <summary>
    /// Per-ply motif sets over the whole replay window: the single-ply shapes DetectAtPly
    /// finds, plus the multi-ply sacrifice family — a sacrifice is only a sacrifice once
    /// the reply and the settled exchange are known, so it cannot be detected ply-locally.
    /// Tags land on the ply of the sacrificing MOVE.
    /// </summary>
    public static IReadOnlyList<string>[] DetectGame(ReplayWindow w)
    {
        var tags = new IReadOnlyList<string>[w.Moves.Count];
        var scratch = new Piece[128];
        for (int i = 0; i < w.Moves.Count; i++)
        {
            List<string>? t = null;
            foreach (var tag in DetectAtPly(w.Boards[i], w.Moves[i], w.Boards[i + 1]))
                (t ??= []).Add(tag);
            DetectSacrifice(w, i, scratch, ref t);
            tags[i] = t ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        return tags;
    }

    // The sacrifice family, all keyed off SEE < 0 (the move statically loses material on
    // its target square):
    //   sacrifice_offered — the opponent declines (no capture on the square next ply);
    //   sacrifice         — accepted, the mover is still down material once the capture
    //                       chain settles, and the eval (when annotated) does not charge
    //                       the mover the full material bill (compensation exists);
    //   queen_sac / exchange_sac — the accepted sacrifice classified by what was given
    //                       (queen; rook for a minor piece);
    //   gambit            — a pawn so offered/given from the standard array while both
    //                       sides retain full castling rights (the structural opening
    //                       marker: no king or rook has moved yet — every classical
    //                       gambit lives there, no arbitrary ply cutoff needed).
    private static void DetectSacrifice(ReplayWindow w, int i, Piece[] scratch, ref List<string>? tags)
    {
        var before = w.Boards[i];
        var m = w.Moves[i];
        if (See.Evaluate(before, m, scratch) >= 0) return;

        Piece moved = before.Squares[m.From];
        bool moverWhite = before.WhiteToMove;
        bool gambit = w.StandardStart && Board.TypeOf(moved) == Piece.WPawn
                      && before.Castle == CastleRights.All;

        bool accepted = i + 1 < w.Moves.Count && CapturesSquare(w.Boards[i + 1], w.Moves[i + 1], m.To);
        if (!accepted)
        {
            // An unanswered final move that ends the game (mate/stalemate: no legal
            // replies) offered nothing — there is no opponent turn left to decline in.
            if (i + 1 == w.Moves.Count && MoveGen.Legal(w.Boards[i + 1]).Count == 0) return;
            (tags ??= []).Add("sacrifice_offered");
            if (gambit) tags.Add("gambit");
            return;
        }

        // The exchange settles when the immediate capture chain stops. The mover must
        // still be down material there — otherwise the exchange recovered itself and
        // nothing was actually given. The quantum is the value table's own pawn.
        int settle = SettleBoard(w, i);
        int loss = Material(w.Boards[i], moverWhite) - Material(w.Boards[settle], moverWhite);
        if (loss < See.ValueOf(Piece.WPawn)) return;
        if (!EvalHolds(w, i, settle, moverWhite, loss)) return;

        (tags ??= []).Add("sacrifice");
        var t = Board.TypeOf(moved);
        if (t == Piece.WQueen) tags.Add("queen_sac");
        else if (t == Piece.WRook && IsMinor(before.Squares[m.To])) tags.Add("exchange_sac");
        if (gambit) tags.Add("gambit");
    }

    private static bool IsMinor(Piece p)
        => Board.TypeOf(p) is Piece.WKnight or Piece.WBishop;

    private static bool IsCapture(Board before, ChessMove m)
        => before.Squares[m.To] != Piece.Empty || (m.Flags & MoveFlags.EnPassant) != 0;

    // Does `reply`, played on `before`, capture the piece standing on `sq`? En passant
    // acceptance lands on the ep square, not the captured pawn's square.
    private static bool CapturesSquare(Board before, ChessMove reply, int sq)
        => (reply.Flags & MoveFlags.EnPassant) != 0
            ? reply.To + (before.WhiteToMove ? -16 : 16) == sq
            : reply.To == sq && before.Squares[sq] != Piece.Empty;

    // Board index after the last capture of the chain that starts with the acceptance at
    // ply i+1 — the first quiet point at which material can be counted.
    private static int SettleBoard(ReplayWindow w, int i)
    {
        int k = i + 1;
        while (k + 1 < w.Moves.Count && IsCapture(w.Boards[k + 1], w.Moves[k + 1])) k++;
        return k + 1;
    }

    /// White-minus-black material in centipawns (kings excluded), sign-flipped to POV.
    private static int Material(Board b, bool whitePov)
    {
        int mat = 0;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            var p = b.Squares[sq];
            if (p == Piece.Empty || Board.TypeOf(p) == Piece.WKing) continue;
            mat += Board.IsWhite(p) ? See.ValueOf(p) : -See.ValueOf(p);
        }
        return whitePov ? mat : -mat;
    }

    // A sacrifice is material given for POSITION: the engine's verdict must not charge
    // the mover the full material bill. An eval drop >= the material lost means the
    // engine sees no compensation — a plain blunder, not a sacrifice. Absent or sparse
    // evals corroborate vacuously (the material window alone decides).
    private static bool EvalHolds(ReplayWindow w, int i, int settleBoard, bool moverWhite, int lossCp)
    {
        var e = w.EvalsWhiteCp;
        if (e is null) return true;
        int afterIdx = settleBoard - 1;   // the eval annotated on the chain's last ply
        if (afterIdx >= e.Length) return true;
        int beforeCp = i > 0 && i - 1 < e.Length ? e[i - 1] : 0;
        int sign = moverWhite ? 1 : -1;
        int drop = sign * beforeCp - sign * e[afterIdx];
        return drop < lossCp;
    }
}
