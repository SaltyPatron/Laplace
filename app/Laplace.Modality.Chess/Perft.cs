namespace Laplace.Modality.Chess;

public static class Perft
{
    /// <summary>Count leaf nodes at the given depth (root not counted). Standard perft.</summary>
    public static long Run(Board b, int depth)
    {
        if (depth == 0) return 1;
        var moves = MoveGen.Legal(b);
        if (depth == 1) return moves.Count;
        long nodes = 0;
        foreach (var m in moves)
        {
            var undo = MoveApply.MakeWithUndo(b, m);
            nodes += Run(b, depth - 1);
            MoveApply.Unmake(b, m, undo);
        }
        return nodes;
    }

    /// <summary>Convenience overload from a ChessState.</summary>
    public static long Run(ChessState s, int depth) => Run(s.Board, depth);

    /// <summary>Divide: per-root-move leaf counts (for debugging).</summary>
    public static Dictionary<string, long> Divide(Board b, int depth)
    {
        var result = new Dictionary<string, long>();
        foreach (var m in MoveGen.Legal(b))
        {
            var undo = MoveApply.MakeWithUndo(b, m);
            result[m.ToUci()] = depth <= 1 ? 1 : Run(b, depth - 1);
            MoveApply.Unmake(b, m, undo);
        }
        return result;
    }
}
