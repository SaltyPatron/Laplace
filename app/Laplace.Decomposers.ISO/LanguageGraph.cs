using System.Xml;
using System.Xml.Linq;

namespace Laplace.Decomposers.ISO;

/// <summary>
/// Builds the language reference GRAPH that turns a language into a navigable hub
/// instead of a bare node reachable only by a HAS_LANGUAGE join:
///   • language → script  — from CLDR supplementalData &lt;languageData&gt;, converging on
///     the SAME Unicode script entities (UcdProperties keys them "unicode/script/{name}/v1")
///     via the ISO 15924 code→UCD-name alias in UCD PropertyValueAliases.txt.
///   • individual → macrolanguage — from iso-639-3-macrolanguages.tab.
/// With Unicode's codepoint→script already attested, this completes
/// codepoint→script→language→macrolanguage so the substrate can FILTER/FOCUS by
/// language, script, or macrolanguage structurally — no runtime joins, no lazy edge.
/// All ids derive from the canonical formulas so every edge lands on entities the
/// Unicode layer and the 639-3 pass already created.
/// </summary>
internal static class LanguageGraph
{
    /// <summary>Script entity id — MUST match UnicodeDecomposer/UcdProperties:
    /// <c>unicode/script/{UCD-long-name}/v1</c> (e.g. name "Latin").</summary>
    public static Laplace.Engine.Core.Hash128 ScriptEntityId(string ucdName) =>
        Laplace.Engine.Core.Hash128.OfCanonical($"unicode/script/{ucdName}/v1");

    /// <summary>ISO 15924 script code → UCD script long-name (e.g. "Latn"→"Latin"),
    /// from UCD PropertyValueAliases.txt lines <c>sc ; Latn ; Latin</c>. This is the
    /// authoritative name UnicodeDecomposer keyed its script entities by, so the link
    /// converges on the real entity rather than a phantom.</summary>
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

    /// <summary>(individual 639-3, macrolanguage 639-3) pairs from
    /// iso-639-3-macrolanguages.tab (header: M_Id\tI_Id\tI_Status).</summary>
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

    /// <summary>(language subtag, script codes) from CLDR supplementalData
    /// &lt;languageData&gt;: <c>&lt;language type="aa" scripts="Latn"/&gt;</c> — scripts
    /// space-separated, both primary and alt="secondary" rows included.</summary>
    public static IEnumerable<(string Lang, string[] Scripts)> LanguageScripts(string iso639Dir)
    {
        string path = Path.Combine(iso639Dir, "cldr", "supplementalData.xml");
        if (!File.Exists(path)) yield break;
        // CLDR files carry a DOCTYPE referencing an external DTD; ignore it (don't fetch).
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
