using System.Text;

namespace Laplace.Modality.Chess;

public static class ChessPgnWriter
{
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

        int fullmove = s.Board.FullmoveNumber;
        bool whiteToMove = s.Board.WhiteToMove;
        bool first = true;
        for (int i = 0; i < moves.Count; i++)
        {
            if (whiteToMove) { sb.Append(fullmove).Append(". "); }
            else if (first) { sb.Append(fullmove).Append("... "); }
            sb.Append(San.ToSan(s.Board, moves[i])).Append(' ');
            s = m.Apply(s, moves[i]);
            if (!whiteToMove) fullmove++;
            whiteToMove = !whiteToMove;
            first = false;
        }
        sb.Append(result).Append("\n\n");
        return sb.ToString();
    }

    public static void WriteFile(
        string path, IEnumerable<(IReadOnlyList<ChessMove> Moves, int Outcome, string StartFen)> games,
        string white = "Laplace", string black = "Laplace", string @event = "Laplace match")
    {
        using var w = new StreamWriter(path, append: false);
        foreach (var (moves, outcome, startFen) in games)
            w.Write(GameToPgn(moves, outcome, startFen, white, black, @event));
    }

    public static void WriteFile(string path, IEnumerable<MatchPgnGame> games, string @event)
    {
        using var w = new StreamWriter(path, append: false);
        foreach (var game in games)
            w.Write(GameToPgn(
                game.Moves, game.Outcome, game.StartFen,
                game.White, game.Black, @event));
    }

    /// <summary>
    /// A game as PGN (the PGN standard's export format): the tag pairs in the order given (the Seven Tag
    /// Roster first, as the caller orders them), then the movetext, each move's SAN numbered from the start
    /// position's move number and side, with the mover's remaining clock as a <c>[%clk h:mm:ss]</c> comment
    /// where one is given, and the result.
    /// </summary>
    public static string Game(
        IReadOnlyList<KeyValuePair<string, string>> tags, IReadOnlyList<string> sans, string result,
        string startFen = ChessModality.StartFen, IReadOnlyList<string?>? clocks = null)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in tags)
            sb.Append('[').Append(name).Append(" \"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\""))
              .Append("\"]\n");
        sb.Append('\n');
        var board = new ChessModality().FromFen(startFen).Board;
        int fullmove = board.FullmoveNumber;
        bool whiteToMove = board.WhiteToMove, first = true;
        for (int i = 0; i < sans.Count; i++)
        {
            if (whiteToMove) sb.Append(fullmove).Append(". ");
            else if (first) sb.Append(fullmove).Append("... ");
            sb.Append(sans[i]).Append(' ');
            if (clocks is not null && i < clocks.Count && clocks[i] is { Length: > 0 } clock)
                sb.Append("{[%clk ").Append(clock).Append("]} ");
            if (!whiteToMove) fullmove++;
            whiteToMove = !whiteToMove;
            first = false;
        }
        sb.Append(result).Append("\n\n");
        return sb.ToString();
    }
}
