namespace Laplace.Modality.Chess;

public static class San
{
    public static ChessMove? Resolve(Board b, IReadOnlyList<ChessMove> legal, string san)
    {
        if (string.IsNullOrWhiteSpace(san)) return null;
        string t = Strip(san.Trim());
        if (t.Length == 0) return null;

        if (t is "O-O-O" or "0-0-0")
            return Single(legal, m => m.IsQueenSideCastle);
        if (t is "O-O" or "0-0")
            return Single(legal, m => m.IsKingSideCastle);

        if (!TryParsePieceMove(t, out var pieceType, out int dest, out int fromFile, out int fromRank,
                out var promoType))
            return null;

        return Single(legal, m => MatchesPieceMove(b, m, pieceType, dest, fromFile, fromRank, promoType));
    }

    /// <summary>
    /// PGN replay path: resolve SAN without generating the full legal list.
    /// Scans only pieces of the SAN type, generates from those squares, legality via
    /// <see cref="MoveGen.IsLegal"/>.
    /// </summary>
    public static ChessMove? Resolve(Board b, string san, List<ChessMove> scratch)
    {
        if (string.IsNullOrWhiteSpace(san)) return null;
        string t = Strip(san.Trim());
        if (t.Length == 0) return null;

        bool white = b.WhiteToMove;
        if (t is "O-O-O" or "0-0-0" or "O-O" or "0-0")
        {
            int kingSq = b.FindKing(white);
            if (kingSq < 0) return null;
            MoveGen.PseudoFrom(b, kingSq, scratch);
            bool kingSide = t is not ("O-O-O" or "0-0-0");
            ChessMove? hit = null;
            for (int i = 0; i < scratch.Count; i++)
            {
                var m = scratch[i];
                if (kingSide ? !m.IsKingSideCastle : !m.IsQueenSideCastle) continue;
                if (!MoveGen.IsLegal(b, m)) continue;
                if (hit is not null) return null;
                hit = m;
            }
            return hit;
        }

        if (!TryParsePieceMove(t, out var pieceType, out int dest, out int fromFile, out int fromRank,
                out var promoType))
            return null;

        Piece colored = white ? pieceType : (Piece)(-(sbyte)pieceType);
        ulong bb = b.PieceBB(colored);
        ChessMove? unique = null;
        while (bb != 0)
        {
            int bit = System.Numerics.BitOperations.TrailingZeroCount(bb);
            bb &= bb - 1;
            int from = Board.Sq(bit & 7, bit >> 3);
            if (fromFile >= 0 && Board.FileOf(from) != fromFile) continue;
            if (fromRank >= 0 && Board.RankOf(from) != fromRank) continue;
            MoveGen.PseudoFrom(b, from, scratch);
            for (int i = 0; i < scratch.Count; i++)
            {
                var m = scratch[i];
                if (!MatchesPieceMove(b, m, pieceType, dest, fromFile, fromRank, promoType)) continue;
                if (!MoveGen.IsLegal(b, m)) continue;
                if (unique is not null) return null;
                unique = m;
            }
        }
        return unique;
    }

    private static bool TryParsePieceMove(string t, out Piece pieceType, out int dest,
        out int fromFile, out int fromRank, out Piece promoType)
    {
        pieceType = Piece.WPawn;
        dest = -1;
        fromFile = fromRank = -1;
        promoType = Piece.Empty;

        int eq = t.IndexOf('=');
        if (eq >= 0 && eq + 1 < t.Length) { promoType = PieceFromChar(t[eq + 1]); t = t[..eq]; }

        int i = 0;
        if (t.Length > 0 && "NBRQK".IndexOf(t[0]) >= 0) { pieceType = PieceFromChar(t[0]); i = 1; }

        string body = t[i..].Replace("x", "");
        if (body.Length < 2) return false;

        string destAlg = body[^2..];
        if (destAlg[0] < 'a' || destAlg[0] > 'h' || destAlg[1] < '1' || destAlg[1] > '8') return false;
        dest = Board.AlgebraicToSquare(destAlg);

        string disamb = body[..^2];
        foreach (char c in disamb)
        {
            if (c >= 'a' && c <= 'h') fromFile = c - 'a';
            else if (c >= '1' && c <= '8') fromRank = c - '1';
        }
        return true;
    }

    private static bool MatchesPieceMove(Board b, ChessMove m, Piece pieceType, int dest,
        int fromFile, int fromRank, Piece promoType)
    {
        // A CASTLE IS ONLY EVER SPELLED O-O / O-O-O, so a piece-move SAN must never
        // match one. Chess960 collides (e.g. "Kc1" vs queen-side castle ending on c1).
        if (m.IsCastle) return false;
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
    }

    public static string ToSan(Board b, ChessMove m)
    {
        if (m.IsKingSideCastle) return WithCheckGlyph(b, m, "O-O");
        if (m.IsQueenSideCastle) return WithCheckGlyph(b, m, "O-O-O");

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
                if (fileUnique) sb.Append((char)('a' + Board.FileOf(m.From)));
                else if (rankUnique) sb.Append((char)('1' + Board.RankOf(m.From)));
                else sb.Append((char)('a' + Board.FileOf(m.From))).Append((char)('1' + Board.RankOf(m.From)));
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
        'N' => Piece.WKnight,
        'B' => Piece.WBishop,
        'R' => Piece.WRook,
        'Q' => Piece.WQueen,
        'K' => Piece.WKing,
        'P' => Piece.WPawn,
        _ => Piece.Empty,
    };
}
