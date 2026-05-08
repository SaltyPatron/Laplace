namespace Laplace.Decomposers.Wiktionary;

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// Streaming parser for kaikki.org Wiktionary JSONL dumps. One
/// <see cref="WiktionaryEntryRecord"/> per non-empty line. Schema is
/// permissive: missing fields default to empty collections / strings; the
/// decomposer is responsible for skipping entries that lack the surface
/// text it needs to F1-decompose.
/// </summary>
public sealed class WiktionaryJsonlParser
{
    public static IEnumerable<WiktionaryEntryRecord> Parse(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) { continue; }
            WiktionaryEntryRecord? record;
            try
            {
                using var doc = JsonDocument.Parse(line);
                record = MapDocument(doc.RootElement);
            }
            catch (JsonException)
            {
                continue; // skip malformed lines without aborting the stream
            }
            if (record is not null) { yield return record; }
        }
    }

    private static WiktionaryEntryRecord? MapDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) { return null; }

        var word     = ReadString(root, "word");
        var lang     = ReadString(root, "lang");
        var langCode = ReadString(root, "lang_code");
        var pos      = ReadString(root, "pos");

        if (word.Length == 0) { return null; }

        return new WiktionaryEntryRecord(
            Word:             word,
            Language:         lang,
            LanguageCode:     langCode,
            Pos:              pos,
            EtymologyText:    ReadString(root, "etymology_text"),
            Glosses:          ReadGlosses(root),
            Pronunciations:   ReadPronunciations(root),
            Forms:            ReadForms(root),
            Synonyms:         ReadRelations(root, "synonyms"),
            Hypernyms:        ReadRelations(root, "hypernyms"),
            Hyponyms:         ReadRelations(root, "hyponyms"),
            Meronyms:         ReadRelations(root, "meronyms"),
            Holonyms:         ReadRelations(root, "holonyms"),
            CoordinateTerms:  ReadRelations(root, "coordinate_terms"),
            Translations:     ReadTranslations(root));
    }

    /// <summary>
    /// Extract IPA pronunciation strings from <c>sounds[].ipa</c>. Each
    /// pronunciation tag set (Received-Pronunciation, General-American,
    /// etc.) is currently dropped; the primary attestation is the IPA
    /// string itself, which content-addresses through F1.
    /// </summary>
    private static IReadOnlyList<string> ReadPronunciations(JsonElement obj)
    {
        if (!obj.TryGetProperty("sounds", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return System.Array.Empty<string>();
        }
        var result = new List<string>(arr.GetArrayLength());
        foreach (var sound in arr.EnumerateArray())
        {
            if (sound.ValueKind != JsonValueKind.Object) { continue; }
            var ipa = ReadString(sound, "ipa");
            if (!string.IsNullOrEmpty(ipa)) { result.Add(ipa); }
        }
        return result;
    }

    /// <summary>
    /// Extract inflection / alternative-spelling form strings from
    /// <c>forms[].form</c>. Tags (plural, alternative, comparative, etc.)
    /// are dropped this slice; primary attestation is the form's surface
    /// text.
    /// </summary>
    private static IReadOnlyList<string> ReadForms(JsonElement obj)
    {
        if (!obj.TryGetProperty("forms", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return System.Array.Empty<string>();
        }
        var result = new List<string>(arr.GetArrayLength());
        foreach (var form in arr.EnumerateArray())
        {
            if (form.ValueKind != JsonValueKind.Object) { continue; }
            var f = ReadString(form, "form");
            if (!string.IsNullOrEmpty(f)) { result.Add(f); }
        }
        return result;
    }

    /// <summary>
    /// Flatten one gloss per sense out of <c>senses[].glosses[0]</c>. Each
    /// sense in Wiktionary may carry multiple gloss strings; the primary
    /// (first) gloss is the canonical short definition, with subsequent
    /// entries typically refinements or alternate phrasings. Substrate
    /// emission attaches one has_sense edge per primary gloss; alternates
    /// can land in a follow-up slice.
    /// </summary>
    private static IReadOnlyList<string> ReadGlosses(JsonElement obj)
    {
        if (!obj.TryGetProperty("senses", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return System.Array.Empty<string>();
        }
        var result = new List<string>(arr.GetArrayLength());
        foreach (var sense in arr.EnumerateArray())
        {
            if (sense.ValueKind != JsonValueKind.Object) { continue; }
            if (!sense.TryGetProperty("glosses", out var glosses) || glosses.ValueKind != JsonValueKind.Array) { continue; }
            foreach (var g in glosses.EnumerateArray())
            {
                if (g.ValueKind == JsonValueKind.String)
                {
                    var s = g.GetString();
                    if (!string.IsNullOrEmpty(s)) { result.Add(s); break; }
                }
            }
        }
        return result;
    }

    private static string ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop)) { return string.Empty; }
        return prop.ValueKind == JsonValueKind.String ? (prop.GetString() ?? string.Empty) : string.Empty;
    }

    private static IReadOnlyList<WiktionaryRelation> ReadRelations(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return System.Array.Empty<WiktionaryRelation>();
        }
        var result = new List<WiktionaryRelation>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { continue; }
            var word = ReadString(item, "word");
            if (word.Length == 0) { continue; }
            result.Add(new WiktionaryRelation(word));
        }
        return result;
    }

    private static IReadOnlyList<WiktionaryTranslation> ReadTranslations(JsonElement obj)
    {
        if (!obj.TryGetProperty("translations", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return System.Array.Empty<WiktionaryTranslation>();
        }
        var result = new List<WiktionaryTranslation>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { continue; }
            var word     = ReadString(item, "word");
            var langCode = ReadString(item, "lang_code");
            if (word.Length == 0 || langCode.Length == 0) { continue; }
            result.Add(new WiktionaryTranslation(langCode, word));
        }
        return result;
    }
}
