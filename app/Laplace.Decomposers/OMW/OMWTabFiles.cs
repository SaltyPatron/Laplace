using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.OMW;

public static class OMWTabFiles
{







    internal static readonly string[] TabGlobPatterns =
        ["wn-data-*.tab", "wn-wikt-*.tab", "wn-cldr-*.tab", "wn-nodia-*.tab"];

    // The corpus's own retractions. Same row shape as the data tabs with two extra
    // leading fields (date, action), so the same parser reads them after a slice.
    internal static readonly string[] ChangesGlobPatterns = ["*-changes.tab"];

    // synset-pos \t lemma \t frequency. The corpus's only shipped per-row magnitude.
    internal static readonly string[] FreqGlobPatterns = ["wn-freq-*.tab"];

    private static readonly IngestSourceLayout FreqLayout = new()
    {
        Files = [.. FreqGlobPatterns.Select(p => IngestFileMatch.Glob(p))],
        Search = SearchOption.AllDirectories,
    };

    public static IEnumerable<string> EnumerateFreqFiles(string wnsDir)
        => IngestInput.FilesIn(wnsDir, FreqLayout);

    private static readonly IngestSourceLayout ChangesLayout = new()
    {
        Files = [.. ChangesGlobPatterns.Select(p => IngestFileMatch.Glob(p))],
        Search = SearchOption.AllDirectories,
    };

    public static IEnumerable<string> EnumerateChangesFiles(string wnsDir)
        => IngestInput.FilesIn(wnsDir, ChangesLayout);

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
