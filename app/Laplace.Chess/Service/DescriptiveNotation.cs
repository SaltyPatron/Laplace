using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// English descriptive notation → legal move, resolved against the current board. The public
/// chess literature (Capablanca, Lasker-era texts) writes "P-K4", "Kt-KB3", "R - R 7", "PxP",
/// "Castles" — squares named from the mover's side, piece origins named by wing. Resolution is
/// legality-driven: a token grounds only when exactly one legal move satisfies it; anything
/// ambiguous or out of vocabulary returns null and the caller truncates the line there. That
/// conservatism is the point — the book lane attests only what replays deterministically.
/// </summary>
public static class DescriptiveNotation
{
    public static ChessMove? Resolve(Board board, IReadOnlyList<ChessMove> legal, string token)
    {
        string t = Normalize(token);
        if (t.Length == 0) return null;

        if (t is "O-O" or "CASTLES-K") return Unique(legal, m => (m.Flags & MoveFlags.CastleKing) != 0);
        if (t is "O-O-O" or "CASTLES-Q") return Unique(legal, m => (m.Flags & MoveFlags.CastleQueen) != 0);
        if (t == "CASTLES")
            return Unique(legal, m => (m.Flags & (MoveFlags.CastleKing | MoveFlags.CastleQueen)) != 0);

        bool enPassant = false;
        if (t.EndsWith("EP", StringComparison.Ordinal)) { enPassant = true; t = t[..^2]; }

        Piece promotion = Piece.Empty;
        int paren = t.IndexOf('(');
        if (paren >= 0 && t.EndsWith(")", StringComparison.Ordinal))
        {
            promotion = PieceOf(t[(paren + 1)..^1]);
            t = t[..paren];
        }
        int eq = t.IndexOfAny(['=', '/']);
        if (eq > 0 && eq == t.Length - 2)
        {
            promotion = PieceOf(t[(eq + 1)..]);
            t = t[..eq];
        }

        int x = t.IndexOf('x');
        if (x < 0) x = t.IndexOf('X');
        if (x > 0) return ResolveCapture(board, legal, t[..x], t[(x + 1)..], enPassant, promotion);

        int dash = t.IndexOf('-');
        if (dash <= 0 || dash == t.Length - 1) return null;
        return ResolveMove(board, legal, t[..dash], t[(dash + 1)..], promotion);
    }

    private static ChessMove? ResolveMove(
        Board board, IReadOnlyList<ChessMove> legal, string pieceSpec, string squareSpec, Piece promotion)
    {
        if (ParsePieceSpec(pieceSpec) is not { } spec) return null;
        if (ParseSquareSpec(squareSpec, board.WhiteToMove) is not { } sq) return null;

        var candidates = Filter(board, legal, spec, m =>
            board.Squares[m.To] == Piece.Empty
            && (m.Flags & MoveFlags.EnPassant) == 0
            && MatchesTarget(m.To, sq));
        return PickWithPromotion(candidates, promotion);
    }

    private static ChessMove? ResolveCapture(
        Board board, IReadOnlyList<ChessMove> legal, string pieceSpec, string victimSpec,
        bool enPassant, Piece promotion)
    {
        if (ParsePieceSpec(pieceSpec) is not { } spec) return null;

        // Victim is usually a piece spec ("KtxP"); a square spec ("KtxP(K4)") already got its
        // parenthetical stripped as a false promotion — accept a square spec here too.
        PieceSpec? victim = ParsePieceSpec(victimSpec);
        (int FileA, int FileB, int Rank)? victimSq =
            victim is null ? ParseSquareSpec(victimSpec, board.WhiteToMove) : null;
        if (victim is null && victimSq is null) return null;

        var candidates = Filter(board, legal, spec, m =>
        {
            bool isEp = (m.Flags & MoveFlags.EnPassant) != 0;
            if (board.Squares[m.To] == Piece.Empty && !isEp) return false;
            if (enPassant && !isEp) return false;
            if (victimSq is { } vs) return MatchesTarget(m.To, vs);
            var victimType = isEp ? Piece.WPawn : Board.TypeOf(board.Squares[m.To]);
            return victimType == victim!.Value.Type;
        });
        return PickWithPromotion(candidates, promotion);
    }

    private static ChessMove? PickWithPromotion(List<ChessMove> candidates, Piece promotion)
    {
        if (promotion != Piece.Empty)
            candidates = candidates.Where(m => m.IsPromotion && Board.TypeOf(m.Promotion) == promotion).ToList();
        else if (candidates.Any(m => m.IsPromotion))
            // Un-annotated promotion defaults to the queen, matching the era's convention.
            candidates = candidates.Where(m => !m.IsPromotion || Board.TypeOf(m.Promotion) == Piece.WQueen).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static List<ChessMove> Filter(
        Board board, IReadOnlyList<ChessMove> legal, PieceSpec spec, Func<ChessMove, bool> pred)
    {
        var all = legal.Where(m => Board.TypeOf(board.Squares[m.From]) == spec.Type && pred(m)).ToList();
        if (all.Count <= 1 || (spec.FromFileA < 0 && spec.FromFileB < 0)) return all;

        // Origin hints ("QR-K1", "KBPxP") narrow by from-file; descriptive strictly names the
        // ORIGINAL piece, which is untrackable without history, so treat the hint as advisory:
        // if it eliminates everything, fall back to the unhinted set (and stay ambiguous).
        var hinted = all.Where(m =>
            Board.FileOf(m.From) == spec.FromFileA
            || (spec.FromFileB >= 0 && Board.FileOf(m.From) == spec.FromFileB)).ToList();
        return hinted.Count > 0 ? hinted : all;
    }

    private static bool MatchesTarget(int to, (int FileA, int FileB, int Rank) sq)
    {
        if (Board.RankOf(to) != sq.Rank) return false;
        int file = Board.FileOf(to);
        return file == sq.FileA || (sq.FileB >= 0 && file == sq.FileB);
    }

    private static ChessMove? Unique(IReadOnlyList<ChessMove> legal, Func<ChessMove, bool> pred)
    {
        ChessMove? found = null;
        foreach (var m in legal)
        {
            if (!pred(m)) continue;
            if (found is not null) return null;
            found = m;
        }
        return found;
    }

    private readonly record struct PieceSpec(Piece Type, int FromFileA, int FromFileB);

    // "P", "K", "Q", "R", "B", "N" (Kt canonicalized by Normalize), wing-prefixed "QR"/"KN"/
    // "KB", and pawn-by-file "QRP"/"KBP"/"RP"/"BP"/"NP". Piece type plus from-file hint(s).
    private static PieceSpec? ParsePieceSpec(string s)
    {
        if (s.Length == 0) return null;

        bool pawn = s.EndsWith("P", StringComparison.Ordinal) && s.Length > 1;
        if (pawn) s = s[..^1];

        int wing = -1; // 0 = queenside, 1 = kingside
        if (s.Length > 1 && (s[0] == 'Q' || s[0] == 'K')) { wing = s[0] == 'Q' ? 0 : 1; s = s[1..]; }

        if (pawn)
        {
            if (s.Length == 0)
                // "QP"/"KP" (wing consumed above) or bare "P".
                return wing switch
                {
                    0 => new PieceSpec(Piece.WPawn, 3, -1),
                    1 => new PieceSpec(Piece.WPawn, 4, -1),
                    _ => new PieceSpec(Piece.WPawn, -1, -1),
                };
            int baseFile = s switch { "R" => 0, "KT" or "N" => 1, "B" => 2, _ => -1 };
            if (baseFile < 0) return null;
            return wing switch
            {
                0 => new PieceSpec(Piece.WPawn, baseFile, -1),
                1 => new PieceSpec(Piece.WPawn, 7 - baseFile, -1),
                _ => new PieceSpec(Piece.WPawn, baseFile, 7 - baseFile),
            };
        }

        if (s.Length == 0)
            // A bare wing letter is the piece itself: "Q" queen, "K" king.
            return wing == 0 ? new PieceSpec(Piece.WQueen, -1, -1)
                 : wing == 1 ? new PieceSpec(Piece.WKing, -1, -1)
                 : null;

        Piece type = PieceOf(s);
        if (type == Piece.Empty) return null;
        if (wing < 0 || type is Piece.WQueen or Piece.WKing)
            return new PieceSpec(type, -1, -1);
        int baseF = type switch { Piece.WRook => 0, Piece.WKnight => 1, Piece.WBishop => 2, _ => -1 };
        if (baseF < 0) return new PieceSpec(type, -1, -1);
        return new PieceSpec(type, wing == 0 ? baseF : 7 - baseF, -1);
    }

    private static Piece PieceOf(string s) => s switch
    {
        "P" => Piece.WPawn,
        "KT" or "N" => Piece.WKnight,
        "B" => Piece.WBishop,
        "R" => Piece.WRook,
        "Q" => Piece.WQueen,
        "K" => Piece.WKing,
        _ => Piece.Empty,
    };

    // Descriptive square: optional wing + file letter + rank digit, rank counted from the
    // MOVER's side. "K4" (white) = e4; "K4" (black) = e5. A bare R/Kt/B file with no wing
    // prefix ("R7") names either wing's file, so FileB carries the mirror; an explicit wing
    // ("QR7") pins one file and FileB stays -1.
    private static (int FileA, int FileB, int Rank)? ParseSquareSpec(string s, bool whiteToMove)
    {
        if (s.Length < 2) return null;
        char rankCh = s[^1];
        if (rankCh is < '1' or > '8') return null;
        int relRank = rankCh - '1';
        int rank = whiteToMove ? relRank : 7 - relRank;

        string filePart = s[..^1];
        int wing = -1;
        if (filePart.Length > 1 && (filePart[0] == 'Q' || filePart[0] == 'K'))
        {
            wing = filePart[0] == 'Q' ? 0 : 1;
            filePart = filePart[1..];
        }

        if (filePart.Length == 0)
        {
            // "Q4" = d-file, "K4" = e-file (the wing letter was the file letter).
            int central = wing == 0 ? 3 : wing == 1 ? 4 : -1;
            return central < 0 ? null : (central, -1, rank);
        }

        int baseF = filePart switch { "R" => 0, "KT" or "N" => 1, "B" => 2, "Q" => 3, "K" => 4, _ => -1 };
        if (baseF < 0) return null;
        if (baseF >= 3) return (baseF, -1, rank); // central files never mirror
        return wing switch
        {
            0 => (baseF, -1, rank),
            1 => (7 - baseF, -1, rank),
            _ => (baseF, 7 - baseF, rank),
        };
    }

    // Uppercase, strip whitespace/periods, unify dashes, drop check/mate/annotation suffixes.
    private static string Normalize(string token)
    {
        Span<char> buf = stackalloc char[token.Length];
        int n = 0;
        foreach (char c in token)
        {
            if (char.IsWhiteSpace(c) || c == '.') continue;
            char u = c switch
            {
                '–' or '—' or '−' => '-', // en/em dash, minus
                '0' when n == 0 || buf[n - 1] == '-' => 'O', // "0-0" castling
                _ => char.ToUpperInvariant(c),
            };
            buf[n++] = u;
        }
        var s = new string(buf[..n]);
        s = s.TrimEnd('!', '?', '+', '#', ',', ';', ':');
        foreach (var suffix in (string[])["DBLCH", "DISCH", "DIS", "CH", "MATE"])
            if (s.EndsWith(suffix, StringComparison.Ordinal) && s.Length > suffix.Length)
            {
                s = s[..^suffix.Length];
                break;
            }
        // "Kt" is the era's knight letter — canonicalize to N BEFORE any prefix parsing, or the
        // wing-stripper eats the K of "Kt-KB3". "PxP e.p." arrives as "PXPEP" — keep EP;
        // Resolve strips it explicitly.
        return s.Replace("KT", "N").Replace("X", "x");
    }
}
