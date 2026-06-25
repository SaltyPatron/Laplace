using System.Text;

namespace Laplace.Modality.Chess;

/// <summary>
/// The position's canonical <b>content surface</b>: the chess substructures it composes from, as a
/// deterministic space-separated token sequence. The universal substrate composer segments it into
/// words → the position entity is the Merkle/centroid composition of these substructure words, so
/// identical substructures across positions and games collapse to ONE shared content node (real
/// pattern sharing + emergent S³ geometry), never a blind FEN-string hash.
///
/// Tokens (canonical order):
///   stm:&lt;w|b&gt;  cr:&lt;KQkq|-&gt;  ep:&lt;sq|-&gt;        — side / rights / live en-passant
///   &lt;piece&gt;&lt;sq&gt; …  (a1→h8 order)               — exact placement: faithful identity + piece-level sharing
///   wpawns:… / bpawns:…                          — pawn skeletons: pawn-structure sharing
///   wpf:dNiNpN / bpf:…                           — bit-banged pawn features (doubled/isolated/passed)
///   mat:…                                        — material signature: material-balance sharing
///
/// Faithful: the placement + stm + cr + ep tokens uniquely determine the position (transposition-stable
/// because every token is a function of the position only — counters/history excluded). The pawn/
/// material tokens are derived and redundant for identity, present purely to create shared sub-nodes.
/// </summary>
public static class PositionContent
{
    public static string Surface(Board b, string canonicalEp)
    {
        var bb = Bitboards.FromBoard(b);
        var sb = new StringBuilder(192);

        sb.Append("stm:").Append(b.WhiteToMove ? 'w' : 'b');
        sb.Append(" cr:").Append(b.CastleString());
        sb.Append(" ep:").Append(canonicalEp);

        // Exact placement — one token per occupied square, a1→h8 (the faithful, piece-level-shared core).
        foreach (int bit in Bitboards.Bits(bb.Occupied))
        {
            int f = Bitboards.FileOfBit(bit), r = Bitboards.RankOfBit(bit);
            sb.Append(' ').Append(Board.PieceToChar(b.Squares[Board.Sq(f, r)])).Append(Alg(f, r));
        }

        // Pawn skeletons (a shared node per distinct pawn placement, per side).
        AppendPawns(sb, " wpawns:", bb.Of(Piece.WPawn));
        AppendPawns(sb, " bpawns:", bb.Of(Piece.BPawn));

        // Bit-banged pawn features → shared pattern nodes.
        ulong wp = bb.Of(Piece.WPawn), bp = bb.Of(Piece.BPawn);
        sb.Append(" wpf:d").Append(Bitboards.Doubled(wp))
          .Append('i').Append(Bitboards.Isolated(wp))
          .Append('p').Append(Bitboards.Passed(wp, bp, white: true));
        sb.Append(" bpf:d").Append(Bitboards.Doubled(bp))
          .Append('i').Append(Bitboards.Isolated(bp))
          .Append('p').Append(Bitboards.Passed(bp, wp, white: false));

        // Material signature (shared node per material balance).
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

        return sb.ToString();
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
