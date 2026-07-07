using System.IO;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public static class OpeningSeed
{
    public static string DefaultDir => Path.Combine(LaplaceInstall.ResolveChessGamesDir(), "openings");

    public static IReadOnlyList<string> Fens(string? path = null, int plies = 10, int max = 0)
    {
        path ??= DefaultDir;
        var m = new ChessModality();
        var seen = new HashSet<string>();
        var fens = new List<string>();
        foreach (var file in Files(path))
            foreach (var line in File.ReadLines(file))
            {
                if (ChessOpeningsDecomposer.ParseRow(line) is not { } row) continue;
                if (!TryReplay(m, ChessOpeningsDecomposer.ExtractSans(row.Movetext), plies, out var fen)) continue;
                if (seen.Add(fen)) fens.Add(fen);
                if (max > 0 && fens.Count >= max) return fens;
            }
        return fens;
    }

    private static bool TryReplay(ChessModality m, List<string> sans, int plies, out string fen)
    {
        var s = m.Initial();
        int n = 0;
        foreach (var san in sans)
        {
            if (n >= plies) break;
            var mv = San.Resolve(s.Board, m.LegalActions(s), san);
            if (mv is null) { fen = ""; return false; }
            s = m.Apply(s, mv.Value);
            n++;
        }
        fen = s.Board.ToFen();
        return n > 0;
    }

    private static IEnumerable<string> Files(string path)
    {
        if (File.Exists(path)) return new[] { path };
        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "*.tsv", SearchOption.AllDirectories)
                            .OrderBy(p => p, StringComparer.Ordinal);
        return Array.Empty<string>();
    }
}
