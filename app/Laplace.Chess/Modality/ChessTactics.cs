namespace Laplace.Modality.Chess;

public readonly record struct EpdPosition(string Fen, IReadOnlyList<string> Best, IReadOnlyList<string> Avoid, string Id);

public readonly record struct TacticResult(string Id, bool Solved, string Engine, string Expected, int Score);

public static class ChessTactics
{
    public static readonly IReadOnlyList<string> Builtin = new[]
    {
        "6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - bm Ra8#; id \"back-rank mate-in-1\";",
        "7k/6pp/8/8/8/8/6PP/3R3K w - - bm Rd8#; id \"corridor mate-in-1\";",
        "k7/8/1K6/8/8/8/8/7R w - - bm Rh8#; id \"R+K box mate-in-1\";",
        "6k1/5ppp/8/8/8/8/5PPP/4R1K1 w - - bm Re8#; id \"back-rank mate-in-1 (rook)\";",
    };

    public static EpdPosition? Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return null;
        var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return null;
        string fen = $"{parts[0]} {parts[1]} {parts[2]} {parts[3]} 0 1";

        var best = new List<string>(); var avoid = new List<string>(); string id = "";
        if (parts.Length == 5)
            foreach (var op in parts[4].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int sp = op.IndexOf(' ');
                if (sp <= 0) continue;
                string code = op[..sp], arg = op[(sp + 1)..].Trim().Trim('"');
                if (code == "bm") best.AddRange(arg.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                else if (code == "am") avoid.AddRange(arg.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                else if (code == "id") id = arg;
            }
        return new EpdPosition(fen, best, avoid, id);
    }

    public static TacticResult Solve(EpdPosition pos, int depth)
    {
        var board = Board.FromFen(pos.Fen);
        var legal = MoveGen.Legal(board);
        var r = new Search(EvalTerm.All).Think(board, new Search.Limits(MaxDepth: depth));
        string engine = r.BestMove?.ToUci() ?? "(none)";

        var bestUci = pos.Best.Select(s => San.Resolve(board, legal, s)?.ToUci()).Where(u => u is not null).ToHashSet();
        var avoidUci = pos.Avoid.Select(s => San.Resolve(board, legal, s)?.ToUci()).Where(u => u is not null).ToHashSet();
        bool solved = bestUci.Count > 0 ? bestUci.Contains(engine)
                    : avoidUci.Count > 0 && !avoidUci.Contains(engine);
        return new TacticResult(pos.Id, solved, engine, string.Join("/", pos.Best.Concat(pos.Avoid)), r.Score);
    }

    public static (int Solved, int Total, IReadOnlyList<TacticResult> Results) Run(IEnumerable<string> epdLines, int depth)
    {
        var results = new List<TacticResult>();
        foreach (var line in epdLines)
            if (Parse(line) is { } pos) results.Add(Solve(pos, depth));
        return (results.Count(r => r.Solved), results.Count, results);
    }
}
