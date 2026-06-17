using System.Text.Json;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Wiktionary;

internal static class WiktionaryJsonFilter
{
    
    public static bool MatchesLanguageFilter(ReadOnlySpan<byte> json, LanguageFilter langs)
    {
        if (!langs.IsActive) return true;
        if (json.Length == 0 || json[0] != (byte)'{') return false;

        bool? matched = null;
        var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (!reader.ValueTextEquals("lang_code") && !reader.ValueTextEquals("lang")) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) return false;
            matched = langs.MatchesRaw(reader.GetString());
            if (matched == false) return false;
            if (matched == true) return true;
        }
        return matched ?? true;
    }

    internal static bool NeedsLanguagePreFilter(string filePath, LanguageFilter? langs) =>
        langs?.IsActive == true
        && filePath.IndexOf("kaikki.org-dictionary-English", StringComparison.OrdinalIgnoreCase) < 0;
}
