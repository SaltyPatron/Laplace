using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.OMW;

public static class OMWTabFiles
{
    internal static readonly string[] TabGlobPatterns = ["wn-data-*.tab", "wn-wikt-*.tab"];

    public static IEnumerable<string> EnumerateTabFiles(string wnsDir, LanguageFilter? langs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pattern in TabGlobPatterns)
        {
            foreach (string tabFile in Directory.EnumerateFiles(wnsDir, pattern, SearchOption.AllDirectories))
            {
                if (!seen.Add(tabFile)) continue;
                string fileLang = FileLang(tabFile);
                if (langs?.MatchesRaw(fileLang) == false) continue;
                yield return tabFile;
            }
        }
    }

    internal static string FileLang(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int dash = name.LastIndexOf('-');
        return dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : "und";
    }
}
