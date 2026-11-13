using System.IO;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

/// <summary>
/// The Lichess opening catalog: <c>a.tsv</c> through <c>e.tsv</c> in one directory, or one
/// selected file. A missing or partial catalog is an error where it is read.
/// </summary>
public static class OpeningSeed
{
    private static readonly string[] CatalogFileNames = ["a.tsv", "b.tsv", "c.tsv", "d.tsv", "e.tsv"];

    /// <summary>
    /// LAPLACE_CHESS_OPENINGS when declared; otherwise the complete catalog under
    /// LAPLACE_DATA/Games/Chess, then the copy the release installed under its prefix.
    /// </summary>
    public static string DefaultDir
    {
        get
        {
            var configured = ChessRuntimeConfiguration.Read("LAPLACE_CHESS_OPENINGS");
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
            var looked = new List<string>();
            var dataRoot = ChessRuntimeConfiguration.Read("LAPLACE_DATA");
            if (!string.IsNullOrWhiteSpace(dataRoot))
            {
                var resolved = ResolveDefaultDir(Path.Combine(dataRoot.Trim(), "Games", "Chess"));
                if (IsComplete(resolved)) return resolved;
                looked.Add(resolved);
            }
            var prefix = ChessRuntimeConfiguration.InstallPrefix;
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                var bundled = Path.Combine(prefix.Trim(), "share", "laplace", "chess-openings");
                if (IsComplete(bundled)) return bundled;
                looked.Add(bundled);
            }
            throw new DirectoryNotFoundException(
                "No complete opening catalog (a.tsv through e.tsv). Declare LAPLACE_CHESS_OPENINGS; looked in: "
                + (looked.Count == 0 ? "(LAPLACE_DATA and LAPLACE_INSTALL_PREFIX are not declared)" : string.Join(", ", looked)));
        }
    }

    internal static string ResolveDefaultDir(string gamesDir)
    {
        // dataset-estate-refresh acquires the upstream repository as lichess-openings.
        // Keep existing installations using openings when that is the complete corpus.
        var refreshed = Path.Combine(gamesDir, "lichess-openings");
        var legacy = Path.Combine(gamesDir, "openings");
        foreach (var candidate in new[] { refreshed, legacy })
            if (IsComplete(candidate)) return candidate;
        return refreshed;
    }

    private static bool IsComplete(string dir)
        => CatalogFileNames.All(name => File.Exists(Path.Combine(dir, name)));

    public static IReadOnlyList<string> Fens(string? path = null, int plies = 10, int max = 0)
    {
        path ??= DefaultDir;
        var m = new ChessModality();
        var seen = new HashSet<string>();
        var fens = new List<string>();
        foreach (var file in CatalogFiles(path))
            foreach (var line in File.ReadLines(file))
            {
                if (OpeningRows.ParseRow(line) is not { } row) continue;
                if (!TryReplay(m, OpeningRows.ExtractSans(row.Movetext), plies, out var fen)) continue;
                if (seen.Add(fen)) fens.Add(fen);
                if (max > 0 && fens.Count >= max) return fens;
            }
        return fens;
    }

    internal static List<(string Eco, string Name, List<string> Sans)> Lines(string path)
    {
        var list = new List<(string, string, List<string>)>();
        foreach (var file in CatalogFiles(path))
            foreach (var line in File.ReadLines(file))
            {
                if (OpeningRows.ParseRow(line) is not { } row) continue;
                var sans = OpeningRows.ExtractSans(row.Movetext);
                if (sans.Count == 0) continue;
                list.Add((row.Eco, row.Name, sans));
            }
        return list;
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

    internal static IReadOnlyList<string> CatalogFiles(string path)
    {
        if (File.Exists(path)) return [path];
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"The opening catalog does not exist: {Path.GetFullPath(path)}");
        var files = CatalogFileNames.Select(name => Path.Combine(path, name)).ToList();
        var missing = files.Where(file => !File.Exists(file)).Select(Path.GetFileName).ToList();
        if (missing.Count > 0)
            throw new FileNotFoundException(
                $"The opening catalog {Path.GetFullPath(path)} is incomplete; missing {string.Join(", ", missing)}.");
        return files;
    }
}
