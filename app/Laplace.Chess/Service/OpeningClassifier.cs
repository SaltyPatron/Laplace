using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public static class OpeningClassifier
{
    public readonly record struct OpeningMatch(string? Name, string? Eco);

    private static readonly object Gate = new();
    private static List<(string Eco, string Name, List<string> Sans)>? _lines;

    public static OpeningMatch Classify(IReadOnlyList<string> sans, ChessModality? modality = null)
    {
        EnsureLoaded();
        if (_lines is null || _lines.Count == 0 || sans.Count == 0) return default;
        string? bestName = null;
        string? bestEco = null;
        int bestLen = 0;

        foreach (var (eco, name, lineSans) in _lines!)
        {
            if (lineSans.Count > sans.Count) continue;
            bool ok = true;
            for (int i = 0; i < lineSans.Count; i++)
            {
                if (!string.Equals(lineSans[i], sans[i], StringComparison.Ordinal))
                { ok = false; break; }
            }
            if (!ok || lineSans.Count <= bestLen) continue;
            bestLen = lineSans.Count;
            bestName = name;
            bestEco = eco;
        }

        return new OpeningMatch(bestName, bestEco);
    }

    internal static void SetLinesForTest(IEnumerable<(string Eco, string Name, List<string> Sans)> lines)
    {
        lock (Gate) { _lines = lines.ToList(); }
    }

    internal static void ResetCache() { lock (Gate) { _lines = null; } }

    private static void EnsureLoaded()
    {
        if (_lines is not null) return;
        lock (Gate)
        {
            if (_lines is not null) return;
            _lines = LoadLines(OpeningSeed.DefaultDir);
        }
    }

    internal static List<(string Eco, string Name, List<string> Sans)> LoadLines(string path)
    {
        var list = new List<(string, string, List<string>)>();
        if (!Directory.Exists(path) && !File.Exists(path)) return list;
        foreach (var file in OpeningSeedFiles(path))
            foreach (var line in File.ReadLines(file))
            {
                if (ChessOpeningsDecomposer.ParseRow(line) is not { } row) continue;
                var sans = ChessOpeningsDecomposer.ExtractSans(row.Movetext);
                if (sans.Count == 0) continue;
                list.Add((row.Eco, row.Name, sans));
            }
        return list.OrderByDescending(x => x.Item3.Count).ToList();
    }

    private static IEnumerable<string> OpeningSeedFiles(string path)
    {
        if (File.Exists(path)) { yield return path; yield break; }
        if (!Directory.Exists(path)) yield break;
        foreach (var f in Directory.EnumerateFiles(path, "*.tsv", SearchOption.AllDirectories))
            yield return f;
    }
}
