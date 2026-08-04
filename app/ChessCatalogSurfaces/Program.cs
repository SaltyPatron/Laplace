using Laplace.Chess.Service;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;

/// <summary>
/// BUILD INPUT PREP — peer of extracting UCDXML before native emit.
/// 1) tier-2 board surfaces for position floor
/// 2) (from,move)→to transition floor — chess state→state dedupe ROM
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: ChessCatalogSurfaces <openings-dir-or-NONE> <surfaces-out> [transitions-out]");
            return 2;
        }
        string openings = args[0];
        string surfacesOut = args[1];
        string? transitionsOut = args.ElementAtOrDefault(2);
        if (string.IsNullOrEmpty(transitionsOut))
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(surfacesOut));
            transitionsOut = Path.Combine(dir ?? ".", "laplace_chess_transition_perfcache.bin");
        }

        CodepointPerfcache.LoadDefault();
        ChessVocabularyCache.Prime(ChessComposeProbe.Compose);

        var surfaces = new SortedSet<string>(StringComparer.Ordinal);
        var transitions = new Dictionary<Hash128, Hash128>(); // key → to
        var m = new ChessModality();

        void NotePosition(Board board, Hash128 id)
        {
            surfaces.Add(PositionContent.Surface(board, Ep(board)));
            _ = id;
        }

        void WalkLine(IReadOnlyList<string> sans, ChessState start)
        {
            var board = start.Board.Clone();
            var fromId = ChessCompose.PositionId(board);
            NotePosition(board, fromId);
            var pseudo = new List<ChessMove>(64);
            var legal = new List<ChessMove>(64);
            foreach (var san in sans)
            {
                MoveGen.Legal(board, pseudo, legal);
                var mv = San.Resolve(board, legal, san);
                if (mv is null) break;
                Piece moving = board.Squares[mv.Value.From];
                var moveId = ChessCompose.MoveId(moving, mv.Value);
                var key = ChessCompose.TransitionKey(fromId, moveId);
                MoveApply.Make(board, mv.Value);
                var toId = ChessCompose.PositionId(board);
                transitions[key] = toId;
                NotePosition(board, toId);
                fromId = toId;
            }
        }

        // Standard start
        WalkLine(Array.Empty<string>(), m.Initial());
        surfaces.Add(m.StateKey(m.Initial()));

        for (int sp = 0; sp < Chess960Positions.Count; sp++)
        {
            string rank = Chess960Positions.BackRank(sp);
            string black = new string(rank.Select(char.ToLowerInvariant).ToArray());
            string fen = $"{black}/pppppppp/8/8/8/8/PPPPPPPP/{rank} w KQkq - 0 1";
            try
            {
                var st = m.FromFen(fen);
                surfaces.Add(m.StateKey(st));
                _ = ChessCompose.PositionId(st.Board);
            }
            catch { /* skip illegal */ }
        }

        if (!string.Equals(openings, "NONE", StringComparison.Ordinal)
            && Directory.Exists(openings))
        {
            foreach (var file in Directory.EnumerateFiles(openings, "*.tsv")
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (ChessOpeningsDecomposer.ParseRow(line) is not { } row) continue;
                    var sans = ChessOpeningsDecomposer.ExtractSans(row.Movetext);
                    if (sans.Count == 0) continue;
                    WalkLine(sans, m.Initial());
                }
            }
        }
        else if (!string.Equals(openings, "NONE", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"missing openings: {openings}");
            return 2;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(surfacesOut))!);
        File.WriteAllLines(surfacesOut, surfaces);

        var sorted = transitions
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(t => t.Key, Hash128BytewiseComparer.Instance)
            .ToList();
        ChessTransitionFloor.WriteBlob(transitionsOut, sorted);

        Console.Error.WriteLine(
            $"chess_catalog: {surfaces.Count} surfaces -> {surfacesOut}; "
            + $"{sorted.Count} transitions -> {transitionsOut}");
        return 0;
    }

    static string Ep(Board b)
    {
        int ep = ChessModality.CapturableEpSquare(b);
        return ep < 0 ? "-" : Board.SquareToAlgebraic(ep);
    }

    sealed class Hash128BytewiseComparer : IComparer<Hash128>
    {
        public static readonly Hash128BytewiseComparer Instance = new();
        public int Compare(Hash128 x, Hash128 y) => x.CompareToBytewise(y);
    }
}
