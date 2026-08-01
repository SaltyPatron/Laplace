using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.OMW;

public static class OMWTabFiles
{







    internal static readonly string[] TabGlobPatterns =
        ["wn-data-*.tab", "wn-wikt-*.tab", "wn-cldr-*.tab", "wn-nodia-*.tab"];

    private static readonly IngestSourceLayout Layout = new()
    {
        Files = [.. TabGlobPatterns.Select(p => IngestFileMatch.Glob(p))],
        Search = SearchOption.AllDirectories,
    };

    public static IEnumerable<string> EnumerateTabFiles(string wnsDir, LanguageFilter? langs)
    {
        foreach (string tabFile in IngestInput.FilesIn(wnsDir, Layout))
        {
            string fileLang = FileLang(tabFile);
            if (langs?.MatchesRaw(fileLang) == false) continue;
            yield return tabFile;
        }
    }

    internal static string FileLang(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int dash = name.LastIndexOf('-');
        return dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : "und";
    }
}
