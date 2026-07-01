namespace Laplace.Modality.Chess;

public static class San
{
    public static ChessMove? Resolve(Board b, IReadOnlyList<ChessMove> legal, string san)
    {
        if (string.IsNullOrWhiteSpace(san)) return null;
        string t = Strip(san.Trim());
        if (t.Length == 0) return null;

        if (t is "O-O-O" or "0-0-0")
            return Single(legal, m => (m.Flags & MoveFlags.CastleQueen) != 0);
        if (t is "O-O" or "0-0")
            return Single(legal, m => (m.Flags & MoveFlags.CastleKing) != 0);

        Piece promoType = Piece.Empty;
        int eq = t.IndexOf('=');
        if (eq >= 0 && eq + 1 < t.Length) { promoType = PieceFromChar(t[eq + 1]); t = t[..eq]; }

        Piece pieceType = Piece.WPawn;
        int i = 0;
        if (t.Length > 0 && "NBRQK".IndexOf(t[0]) >= 0) { pieceType = PieceFromChar(t[0]); i = 1; }

        string body = t[i..].Replace("x", "");
        if (body.Length < 2) return null;

        string destAlg = body[^2..];
        if (destAlg[0] < 'a' || destAlg[0] > 'h' || destAlg[1] < '1' || destAlg[1] > '8') return null;
        int dest = Board.AlgebraicToSquare(destAlg);

        string disamb = body[..^2];
        int fromFile = -1, fromRank = -1;
        foreach (char c in disamb)
        {
            if (c >= 'a' && c <= 'h') fromFile = c - 'a';
            else if (c >= '1' && c <= '8') fromRank = c - '1';
        }

        return Single(legal, m =>
        {
            if (m.To != dest) return false;
            if (Board.TypeOf(b.Squares[m.From]) != pieceType) return false;
            if (promoType != Piece.Empty)
            {
                if (!m.IsPromotion || Board.TypeOf(m.Promotion) != promoType) return false;
            }
            else if (m.IsPromotion) return false;
            if (fromFile >= 0 && Board.FileOf(m.From) != fromFile) return false;
            if (fromRank >= 0 && Board.RankOf(m.From) != fromRank) return false;
            return true;
        });
    }

    public static string ToSan(Board b, ChessMove m)
    {
        if ((m.Flags & MoveFlags.CastleKing) != 0)  return WithCheckGlyph(b, m, "O-O");
        if ((m.Flags & MoveFlags.CastleQueen) != 0) return WithCheckGlyph(b, m, "O-O-O");

        var legal = MoveGen.Legal(b);
        Piece moving = b.Squares[m.From];
        Piece type = Board.TypeOf(moving);
        bool isPawn = type == Piece.WPawn;
        bool isCapture = b.Squares[m.To] != Piece.Empty || (m.Flags & MoveFlags.EnPassant) != 0;
        string dest = Board.SquareToAlgebraic(m.To);
        var sb = new System.Text.StringBuilder(8);

        if (isPawn)
        {
            if (isCapture) sb.Append((char)('a' + Board.FileOf(m.From))).Append('x');
            sb.Append(dest);
            if (m.IsPromotion) sb.Append('=').Append(char.ToUpperInvariant(Board.PieceToChar(Board.TypeOf(m.Promotion))));
        }
        else
        {
            sb.Append(char.ToUpperInvariant(Board.PieceToChar(type)));
            var rivals = legal.Where(x => x.To == m.To && x.From != m.From
                                          && Board.TypeOf(b.Squares[x.From]) == type).ToList();
            if (rivals.Count > 0)
            {
                bool fileUnique = rivals.All(x => Board.FileOf(x.From) != Board.FileOf(m.From));
                bool rankUnique = rivals.All(x => Board.RankOf(x.From) != Board.RankOf(m.From));
                if (fileUnique)      sb.Append((char)('a' + Board.FileOf(m.From)));
                else if (rankUnique) sb.Append((char)('1' + Board.RankOf(m.From)));
                else                 sb.Append((char)('a' + Board.FileOf(m.From))).Append((char)('1' + Board.RankOf(m.From)));
            }
            if (isCapture) sb.Append('x');
            sb.Append(dest);
        }
        return WithCheckGlyph(b, m, sb.ToString());
    }

    private static string WithCheckGlyph(Board b, ChessMove m, string san)
    {
        var nb = b.Clone();
        MoveApply.Make(nb, m);
        if (!MoveGen.InCheck(nb, nb.WhiteToMove)) return san;
        return MoveGen.Legal(nb).Count == 0 ? san + "#" : san + "+";
    }

    private static ChessMove? Single(IReadOnlyList<ChessMove> legal, Func<ChessMove, bool> pred)
    {
        ChessMove? hit = null;
        foreach (var m in legal)
            if (pred(m)) { if (hit is not null) return null; hit = m; }
        return hit;
    }

    private static string Strip(string s)
    {
        int end = s.Length;
        while (end > 0 && (s[end - 1] is '+' or '#' or '!' or '?')) end--;
        s = s[..end];
        if (s.EndsWith("e.p.", StringComparison.Ordinal)) s = s[..^4];
        return s.Trim();
    }

    private static Piece PieceFromChar(char c) => char.ToUpperInvariant(c) switch
    {
        'N' => Piece.WKnight, 'B' => Piece.WBishop, 'R' => Piece.WRook,
        'Q' => Piece.WQueen,  'K' => Piece.WKing,   'P' => Piece.WPawn,
        _ => Piece.Empty,
    };
}
