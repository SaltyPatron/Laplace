using System.IO;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public static class OpeningSeed
{
    public static string DefaultDir
    {
        get
        {
            var configured = LaplaceInstall.TryReadConfig("LAPLACE_CHESS_OPENINGS", "chess-lab.env");
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            var dataRoot = Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT");
            var gamesDir = !string.IsNullOrWhiteSpace(dataRoot)
                ? Path.Combine(dataRoot.Trim(), "Games", "Chess")
                : LaplaceInstall.ResolveChessGamesDir();
            return ResolveDefaultDir(gamesDir);
        }
    }

    internal static string ResolveDefaultDir(string gamesDir)
    {
        // dataset-estate-refresh acquires the upstream repository as lichess-openings.
        // Keep existing installations using openings when that is the populated corpus.
        var refreshed = Path.Combine(gamesDir, "lichess-openings");
        var legacy = Path.Combine(gamesDir, "openings");
        foreach (var candidate in new[] { refreshed, legacy })
            if (Directory.Exists(candidate)
                && Directory.EnumerateFiles(candidate, "*.tsv", SearchOption.AllDirectories).Any())
                return candidate;
        return refreshed;
    }

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
        // An opening catalog can contain a complete miniature (for example Fool's
        // Mate). Cute Chess still sends "go" from a terminal EPD and records the
        // engine's no-move response as an illegal-move loss. Only ongoing states
        // can seed a new playing; keep scanning the corpus to fill the request.
        return n > 0 && m.Terminal(s) is null;
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
