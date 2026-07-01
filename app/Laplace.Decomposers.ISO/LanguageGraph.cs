using System.Xml;
using System.Xml.Linq;

namespace Laplace.Decomposers.ISO;

internal static class LanguageGraph
{
    public static Laplace.Engine.Core.Hash128 ScriptEntityId(string ucdName) =>
        Laplace.Engine.Core.Hash128.OfCanonical($"unicode/script/{ucdName}/v1");

    public static Laplace.Engine.Core.Hash128 VariantEntityId(string subtag) =>
        Laplace.Engine.Core.Hash128.OfCanonical($"substrate/iso639/variant/{subtag.ToLowerInvariant()}/v1");

    public static Dictionary<string, string> LoadScriptCodeToUcdName(string unidataDir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string pva = Path.Combine(unidataDir, "PropertyValueAliases.txt");
        if (!File.Exists(pva)) return map;
        foreach (var line in File.ReadLines(pva))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(';');
            if (f.Length < 3 || f[0].Trim() != "sc") continue;
            string code = f[1].Trim(), name = f[2].Trim();
            if (code.Length > 0 && name.Length > 0) map[code] = name;
        }
        return map;
    }

    public static IEnumerable<(string Individual, string Macro)> Macrolanguages(string iso639Dir)
    {
        string path = Path.Combine(iso639Dir, "iso-639-3-macrolanguages.tab");
        if (!File.Exists(path)) yield break;
        bool header = false;
        foreach (var line in File.ReadLines(path))
        {
            if (!header) { header = true; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Split('\t');
            if (f.Length < 2) continue;
            string macro = f[0].Trim(), indiv = f[1].Trim();
            if (macro.Length == 3 && indiv.Length == 3) yield return (indiv, macro);
        }
    }

    public static IEnumerable<(string Subtag, string[] Prefixes)> Variants(string iso639Dir)
    {
        string path = Path.Combine(iso639Dir, "iana", "language-subtag-registry.txt");
        if (!File.Exists(path)) yield break;

        string type = "", subtag = "";
        var prefixes = new List<string>();

        foreach (var rawLine in File.ReadLines(path))
        {
            if (rawLine == "%%")
            {
                if (type == "variant" && subtag.Length != 0)
                    yield return (subtag, prefixes.ToArray());
                type = ""; subtag = ""; prefixes.Clear();
                continue;
            }
            int c = rawLine.IndexOf(':');
            if (c <= 0) continue;
            string key = rawLine[..c].Trim();
            string val = rawLine[(c + 1)..].Trim();
            switch (key)
            {
                case "Type":   type = val.ToLowerInvariant(); break;
                case "Subtag": subtag = val; break;
                case "Prefix": prefixes.Add(val); break;
            }
        }
        if (type == "variant" && subtag.Length != 0)
            yield return (subtag, prefixes.ToArray());
    }

    public static IEnumerable<(string Lang, string[] Scripts)> LanguageScripts(string iso639Dir)
    {
        string path = Path.Combine(iso639Dir, "cldr", "supplementalData.xml");
        if (!File.Exists(path)) yield break;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        var doc = XDocument.Load(reader);
        foreach (var ld in doc.Descendants("languageData"))
        foreach (var lang in ld.Elements("language"))
        {
            string? type = (string?)lang.Attribute("type");
            string? scripts = (string?)lang.Attribute("scripts");
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(scripts)) continue;
            var codes = scripts.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (codes.Length > 0) yield return (type!, codes);
        }
    }
}
