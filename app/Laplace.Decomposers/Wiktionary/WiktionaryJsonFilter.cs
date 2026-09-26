using System.Text.Json;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Wiktionary;

internal static class WiktionaryJsonFilter
{

    public static bool MatchesLanguageFilter(ReadOnlySpan<byte> json, LanguageFilter langs)
    {
        if (!langs.IsActive) return true;
        if (json.Length == 0 || json[0] != (byte)'{') return false;

        // The row's own language is stated by its top-level lang (a name) and lang_code (a
        // tag); nested translations name other languages. Either of the row's own tags
        // admits it.
        bool stated = false;
        var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.CurrentDepth != 1) continue;
            if (!reader.ValueTextEquals("lang_code") && !reader.ValueTextEquals("lang")) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
            stated = true;
            if (langs.MatchesRaw(reader.GetString())) return true;
        }
        return !stated;
    }

    internal static bool NeedsLanguagePreFilter(string filePath, LanguageFilter? langs) =>
        langs?.IsActive == true
        && filePath.IndexOf("kaikki.org-dictionary-English", StringComparison.OrdinalIgnoreCase) < 0;
}
