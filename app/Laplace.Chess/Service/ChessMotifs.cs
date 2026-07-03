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
}
