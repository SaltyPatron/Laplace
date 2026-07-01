namespace Laplace.Chess.Service;

public static class ChessMotifs
{
    private static readonly (string Name, string[] Sans)[] Patterns =
    [
        ("ScholarsMate", ["e4", "e5", "Bc4", "Nc6", "Qh5", "Nf6", "Qxf7#"]),
        ("ScholarsMate", ["e4", "e5", "Bc4", "Nc6", "Qh5", "Nf6", "Qxf7"]),
        ("FriedLiver", ["e4", "e5", "Nf3", "Nc6", "Bc4", "Nf6", "Ng5", "d5", "exd5", "Nxd5", "Nxf7"]),
    ];

    public static string? Detect(IReadOnlyList<string> sans)
    {
        foreach (var (name, pattern) in Patterns)
        {
            if (sans.Count < pattern.Length) continue;
            bool ok = true;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (!SanMatch(sans[i], pattern[i])) { ok = false; break; }
            }
            if (ok) return name;
        }
        return null;
    }

    private static bool SanMatch(string played, string pattern)
    {
        if (string.Equals(played, pattern, StringComparison.Ordinal)) return true;
        if (pattern.EndsWith('#') && played.StartsWith(pattern[..^1], StringComparison.Ordinal)) return true;
        return false;
    }
}
