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
            var configured = ChessRuntimeConfiguration.Read("LAPLACE_CHESS_OPENINGS");
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            var dataRoot = ChessRuntimeConfiguration.Read("LAPLACE_DATA")
                ?? ChessRuntimeConfiguration.Read("LAPLACE_DATA_ROOT");
            string gamesDir;
            if (!string.IsNullOrWhiteSpace(dataRoot))
            {
                gamesDir = Path.Combine(dataRoot.Trim(), "Games", "Chess");
            }
            else
            {
                try { gamesDir = LaplaceInstall.ResolveChessGamesDir(); }
                catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException)
                {
                    gamesDir = Path.Combine(ChessLabPaths.LabDir, "Games", "Chess");
                }
            }
            var resolved = ResolveDefaultDir(gamesDir);
            if (Directory.Exists(resolved)
                && Directory.EnumerateFiles(resolved, "*.tsv", SearchOption.AllDirectories).Any())
                return resolved;
            var prefix = ChessRuntimeConfiguration.InstallPrefix;
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                var bundled = Path.Combine(prefix.Trim(), "share", "laplace", "chess-openings");
                if (Directory.Exists(bundled)
                    && Directory.EnumerateFiles(bundled, "*.tsv", SearchOption.AllDirectories).Any())
                    return bundled;
            }
            return resolved;
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
