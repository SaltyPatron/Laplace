using System.Text;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.ConceptNet;

internal static class ConceptNetRowFilter
{
    public static bool MatchesLanguageFilter(ReadOnlySpan<byte> line, LanguageFilter langs)
    {
        if (!langs.IsActive) return true;
        if (!TryConceptLang(line, 2, out var startLang)) return false;
        if (!TryConceptLang(line, 3, out var endLang)) return false;
        return langs.MatchesAll(startLang, endLang);
    }

    public static bool MatchesLanguageFilter(string line, LanguageFilter langs)
        => MatchesLanguageFilter(Encoding.UTF8.GetBytes(line), langs);

    private static bool TryConceptLang(ReadOnlySpan<byte> line, int fieldIndex, out string lang)
    {
        lang = "";
        if (!TryField(line, fieldIndex, out var field)) return false;
        return TryParseConceptLang(field, out lang);
    }

    internal static bool TryParseConceptLang(ReadOnlySpan<byte> uri, out string lang)
    {
        lang = "";
        // /c/{lang}/...
        if (uri.Length < 5 || uri[0] != (byte)'/' || uri[1] != (byte)'c' || uri[2] != (byte)'/') return false;
        int langStart = 3;
        int langEnd = uri[langStart..].IndexOf((byte)'/');
        if (langEnd < 0) return false;
        lang = Encoding.UTF8.GetString(uri.Slice(langStart, langEnd));
        return lang.Length > 0;
    }

    private static bool TryField(ReadOnlySpan<byte> line, int fieldIndex, out ReadOnlySpan<byte> field)
    {
        field = default;
        int tab = 0;
        int start = 0;
        for (int i = 0; i <= line.Length; i++)
        {
            if (i == line.Length || line[i] == (byte)'\t')
            {
                if (tab == fieldIndex)
                {
                    field = line[start..i];
                    return true;
                }
                tab++;
                start = i + 1;
            }
        }
        return false;
    }
}
