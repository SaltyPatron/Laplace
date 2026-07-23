using Laplace.Modality;
using Laplace.Modality.Chess;

namespace Laplace.Chess.Service;

public static class OpeningClassifier
{
    public readonly record struct OpeningMatch(string? Name, string? Eco);

    private static readonly object Gate = new();
    private static Dictionary<string, (string Eco, string Name)>? _byLine;
    private static int _maxLineLen;

    // Longest-prefix lookup over the ECO table: the game's SAN prefix joined with spaces
    // is the key, probed from the longest possible line length downward. O(maxLineLen)
    // dictionary probes per game instead of the old scan over all ~3,733 ECO lines per
    // game (the per-game analyzer hot spot GH #450 measured).
    public static OpeningMatch Classify(IReadOnlyList<string> sans, ChessModality? modality = null)
    {
        EnsureLoaded();
        var byLine = _byLine;
        if (byLine is null || byLine.Count == 0 || sans.Count == 0) return default;

        int maxLen = Math.Min(sans.Count, _maxLineLen);
        var prefixes = new string[maxLen];
        var sb = new System.Text.StringBuilder(maxLen * 5);
        for (int i = 0; i < maxLen; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(sans[i]);
            prefixes[i] = sb.ToString();
        }
        for (int len = maxLen; len >= 1; len--)
            if (byLine.TryGetValue(prefixes[len - 1], out var hit))
                return new OpeningMatch(hit.Name, hit.Eco);
        return default;
    }

    internal static void SetLinesForTest(IEnumerable<(string Eco, string Name, List<string> Sans)> lines)
    {
        lock (Gate) { (_byLine, _maxLineLen) = Index(lines.ToList()); }
    }

    internal static void ResetCache() { lock (Gate) { _byLine = null; _maxLineLen = 0; } }

    private static void EnsureLoaded()
    {
        if (_byLine is not null) return;
        lock (Gate)
        {
            if (_byLine is not null) return;
            (_byLine, _maxLineLen) = Index(LoadLines(OpeningSeed.DefaultDir));
        }
    }

    private static (Dictionary<string, (string, string)>, int) Index(
        List<(string Eco, string Name, List<string> Sans)> lines)
    {
        var map = new Dictionary<string, (string, string)>(lines.Count, StringComparer.Ordinal);
        int maxLen = 0;
        // LoadLines orders longest-first, load-order stable — TryAdd keeps the same winner
        // the old scan picked when two rows share an identical move sequence.
        foreach (var (eco, name, sans) in lines)
        {
            map.TryAdd(string.Join(' ', sans), (eco, name));
            if (sans.Count > maxLen) maxLen = sans.Count;
        }
        return (map, maxLen);
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
