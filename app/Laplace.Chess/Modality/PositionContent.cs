using System.Text;

namespace Laplace.Modality.Chess;

public static class PositionContent
{
    public static bool IncludeFeatureTokens { get; set; }

    public static string Surface(Board b, string canonicalEp)
    {
        var bb = Bitboards.FromBoard(b);
        var sb = new StringBuilder(192);

        sb.Append("stm:").Append(b.WhiteToMove ? 'w' : 'b');
        sb.Append(" cr:").Append(b.CastleString());
        sb.Append(" ep:").Append(canonicalEp);


        foreach (int bit in Bitboards.Bits(bb.Occupied))
        {
            int f = Bitboards.FileOfBit(bit), r = Bitboards.RankOfBit(bit);
            sb.Append(' ').Append(Board.PieceToChar(b.Squares[Board.Sq(f, r)])).Append(Alg(f, r));
        }


        AppendPawns(sb, " wpawns:", bb.Of(Piece.WPawn));
        AppendPawns(sb, " bpawns:", bb.Of(Piece.BPawn));


        ulong wp = bb.Of(Piece.WPawn), bp = bb.Of(Piece.BPawn);
        sb.Append(" wpf:d").Append(Bitboards.Doubled(wp))
          .Append('i').Append(Bitboards.Isolated(wp))
          .Append('p').Append(Bitboards.Passed(wp, bp, white: true));
        sb.Append(" bpf:d").Append(Bitboards.Doubled(bp))
          .Append('i').Append(Bitboards.Isolated(bp))
          .Append('p').Append(Bitboards.Passed(bp, wp, white: false));


        sb.Append(" mat:")
          .Append('P').Append(Bitboards.Count(bb.Of(Piece.WPawn)))
          .Append('N').Append(Bitboards.Count(bb.Of(Piece.WKnight)))
          .Append('B').Append(Bitboards.Count(bb.Of(Piece.WBishop)))
          .Append('R').Append(Bitboards.Count(bb.Of(Piece.WRook)))
          .Append('Q').Append(Bitboards.Count(bb.Of(Piece.WQueen)))
          .Append('p').Append(Bitboards.Count(bb.Of(Piece.BPawn)))
          .Append('n').Append(Bitboards.Count(bb.Of(Piece.BKnight)))
          .Append('b').Append(Bitboards.Count(bb.Of(Piece.BBishop)))
          .Append('r').Append(Bitboards.Count(bb.Of(Piece.BRook)))
          .Append('q').Append(Bitboards.Count(bb.Of(Piece.BQueen)));

        if (IncludeFeatureTokens)
            AppendFeatureTokens(sb, b, bb);

        return sb.ToString();
    }

    private static void AppendFeatureTokens(StringBuilder sb, Board b, Bitboards bb)
    {
        int mob = MoveGen.Legal(b).Count;
        sb.Append(" mob:").Append(mob);

        int open = 0;
        for (int f = 0; f < 8; f++)
        {
            ulong pawns = (bb.Of(Piece.WPawn) | bb.Of(Piece.BPawn)) & Bitboards.FileMask(f);
            if (pawns == 0) open++;
        }
        sb.Append(" open:").Append(open);

        int kingSq = FindKing(b, b.WhiteToMove);
        sb.Append(" kzone:").Append(KingZoneCount(kingSq));

        sb.Append(" outpost:").Append(CountOutposts(bb, b.WhiteToMove));
    }

    private static int FindKing(Board b, bool white)
    {
        var target = white ? Piece.WKing : Piece.BKing;
        for (int sq = 0; sq < 128; sq++)
        {
            if ((sq & 0x88) != 0) { sq += 7; continue; }
            if (b.Squares[sq] == target) return Bitboards.Bit(sq);
        }
        return 0;
    }

    private static int KingZoneCount(int kingBit)
    {
        int f = Bitboards.FileOfBit(kingBit), r = Bitboards.RankOfBit(kingBit);
        int n = 0;
        for (int df = -1; df <= 1; df++)
            for (int dr = -1; dr <= 1; dr++)
            {
                int nf = f + df, nr = r + dr;
                if (nf is >= 0 and <= 7 && nr is >= 0 and <= 7) n++;
            }
        return n;
    }

    private static int CountOutposts(Bitboards bb, bool white)
    {
        ulong minors = bb.Of(white ? Piece.WKnight : Piece.BKnight) | bb.Of(white ? Piece.WBishop : Piece.BBishop);
        ulong own = bb.Of(white ? Piece.WPawn : Piece.BPawn);
        ulong enemy = bb.Of(white ? Piece.BPawn : Piece.WPawn);
        int count = 0;
        foreach (int bit in Bitboards.Bits(minors))
        {
            int r = Bitboards.RankOfBit(bit);
            if (r < 2 || r > 5) continue;
            int f = Bitboards.FileOfBit(bit);
            // A pawn defends an outpost square diagonally from the rank BEHIND it, not its own rank.
            int supportRank = white ? r - 1 : r + 1;
            bool defended = (own & Bitboards.AdjacentFiles(f) & RankMask(supportRank)) != 0;
            bool blocked = (enemy & Bitboards.AdjacentFiles(f) & ForwardRanks(r, white)) != 0;
            if (defended && !blocked) count++;
        }
        return count;
    }

    private static ulong RankMask(int rank) => 0xFFUL << (rank * 8);

    private static ulong ForwardRanks(int rank, bool white)
    {
        if (white)
        {
            int start = (rank + 1) * 8;
            return start < 64 ? (~0UL << start) : 0;
        }
        int end = rank * 8;
        return end > 0 ? (1UL << end) - 1 : 0;
    }

    private static void AppendPawns(StringBuilder sb, string tag, ulong pawns)
    {
        sb.Append(tag);
        bool first = true;
        foreach (int bit in Bitboards.Bits(pawns))
        {
            if (!first) sb.Append('.');
            sb.Append(Alg(Bitboards.FileOfBit(bit), Bitboards.RankOfBit(bit)));
            first = false;
        }
        if (first) sb.Append('-');
    }

    private static string Alg(int file, int rank) => $"{(char)('a' + file)}{(char)('1' + rank)}";
}
