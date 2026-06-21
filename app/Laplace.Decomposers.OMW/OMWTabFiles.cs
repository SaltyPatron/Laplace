using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.OMW;

public static class OMWTabFiles
{
    // wn-data (curated wordnet projects) + wn-wikt (Wiktionary-derived) + wn-cldr (Unicode CLDR-derived,
    // 122 languages — by far the largest non-data/wikt source) + wn-nodia (diacritic-stripped variant,
    // e.g. Arabic) all share the same <offset>-<pos>\t[<lang>:]lemma|def|exe\t<value> row shape that
    // OMWRowParser already parses generically. Deliberately excluded: wn-freq-*.tab (word\tfrequency —
    // a different shape; OMWRowParser's type-field match rejects it, so adding it here would silently
    // ingest nothing rather than ingest wrong data) and <lang>-changes.tab (version changelogs, not
    // lexical content).
    internal static readonly string[] TabGlobPatterns =
        ["wn-data-*.tab", "wn-wikt-*.tab", "wn-cldr-*.tab", "wn-nodia-*.tab"];

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
