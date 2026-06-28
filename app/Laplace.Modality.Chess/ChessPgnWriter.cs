using System.Text;

namespace Laplace.Modality.Chess;

/// <summary>
/// Writes in-process games (self-play, gauntlet matches) as standard PGN — replaying each move list from
/// the start and rendering it via <see cref="San.ToSan"/>. This closes the ingest flywheel: every game our
/// engine produces can be written out and fed back through the <c>pgn</c> tree-sitter grammar
/// (<c>laplace ingest chess</c>), the same path chess.com / lichess archives take. Pure C#, no DB.
/// </summary>
public static class ChessPgnWriter
{
    /// <summary>One game (<paramref name="moves"/> from <paramref name="startFen"/>, <paramref name="outcome"/>
    /// 0=White / 1=Black / 2=draw) as a PGN record. A non-standard start adds the <c>[FEN]</c>/<c>[SetUp]</c>
    /// tags + correct move numbering so the replay (and re-ingest) is faithful.</summary>
    public static string GameToPgn(
        IReadOnlyList<ChessMove> moves, int outcome, string startFen = ChessModality.StartFen,
        string white = "Laplace", string black = "Laplace", string @event = "Laplace match")
    {
        string result = outcome == 0 ? "1-0" : outcome == 1 ? "0-1" : "1/2-1/2";
        var m = new ChessModality();
        var s = m.FromFen(startFen);
        bool custom = startFen != ChessModality.StartFen;

        var sb = new StringBuilder();
        sb.Append("[Event \"").Append(@event).Append("\"]\n");
        sb.Append("[White \"").Append(white).Append("\"]\n");
        sb.Append("[Black \"").Append(black).Append("\"]\n");
        sb.Append("[Result \"").Append(result).Append("\"]\n");
        if (custom) { sb.Append("[SetUp \"1\"]\n"); sb.Append("[FEN \"").Append(startFen).Append("\"]\n"); }
        sb.Append('\n');

        // Move numbering honors the side to move + fullmove number of the start position.
        int fullmove = s.Board.FullmoveNumber;
        bool whiteToMove = s.Board.WhiteToMove;
        bool first = true;
        for (int i = 0; i < moves.Count; i++)
        {
            if (whiteToMove) { sb.Append(fullmove).Append(". "); }
            else if (first)  { sb.Append(fullmove).Append("... "); }   // black-to-move start: "12... Nf6"
            sb.Append(San.ToSan(s.Board, moves[i])).Append(' ');
            s = m.Apply(s, moves[i]);
            if (!whiteToMove) fullmove++;
            whiteToMove = !whiteToMove;
            first = false;
        }
        sb.Append(result).Append("\n\n");
        return sb.ToString();
    }

    /// <summary>Write many games to a PGN file (one record each).</summary>
    public static void WriteFile(
        string path, IEnumerable<(IReadOnlyList<ChessMove> Moves, int Outcome, string StartFen)> games,
        string white = "Laplace", string black = "Laplace", string @event = "Laplace match")
    {
        using var w = new StreamWriter(path, append: false);
        foreach (var (moves, outcome, startFen) in games)
            w.Write(GameToPgn(moves, outcome, startFen, white, black, @event));
    }
}
