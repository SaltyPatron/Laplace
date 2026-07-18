using System.Text.Json;
using Laplace.Decomposers.Abstractions;

namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// One wiktextract JSONL row parsed by a single native <see cref="Utf8JsonReader"/>
/// pass into the exact fields the witness attests — NO tree-sitter, NO per-node
/// managed↔native AST crossings. The 10GB kaikki corpus is structured data, so it
/// rides a data path (streaming UTF-8 reader) rather than the source-code grammar
/// spine. The emitted attestations (content ids, relation types, contexts) are
/// identical to the former grammar-witness output — only the parse changed.
/// </summary>
public sealed class WiktionaryEntry
{
    public string Word = string.Empty;

    /// <summary>lang_code preferred, else lang; null when the row carries neither.</summary>
    public string? LangCode;

    public string? Pos;
    public bool IncludeTranslations;

    public List<Sense>? Senses;
    public List<Sound>? Sounds;
    public List<Form>? Forms;
    public string? EtymologyText;
    public List<EtyTemplate>? EtymologyTemplates;

    /// <summary>Top-level relation blocks (context = null when attested).</summary>
    public RelationBlock Top;

    public List<string>? Translations;

    public sealed class Sense
    {
        public List<string>? Glosses;
        public List<string>? Examples;
        public RelationBlock Relations;
        public List<string>? Tags;
        public List<string>? LinkTargets;
        public string? SynsetKey;
    }

    public struct RelationBlock
    {
        public List<string>? Synonyms;
        public List<string>? Antonyms;
        public List<string>? Hyponyms;
        public List<string>? Meronyms;
        public List<string>? Holonyms;
        public List<string>? Related;
        public List<string>? Hypernyms;
        public List<string>? Derived;
        public List<string>? Coordinate;
    }

    public readonly record struct Sound(string? Ipa, List<string>? Tags);

    public readonly record struct Form(string FormText, List<string>? Tags);

    public sealed class EtyTemplate
    {
        public string? Name;
        public Dictionary<string, string>? Args;
    }

    /// <summary>
    /// Parse one JSONL row. Returns null when the row has no "word" or when a
    /// language filter is active and the row's own top-level language is absent
    /// or does not match — the same gate the witness applied inline.
    /// </summary>
    public static WiktionaryEntry? Parse(ReadOnlySpan<byte> utf8, DecomposerOptions options)
    {
        if (utf8.IsEmpty || utf8[0] != (byte)'{') return null;

        var e = new WiktionaryEntry { IncludeTranslations = options.EmitCrossLanguageLinks };
        string? langCode = null;
        string? lang = null;

        var reader = new Utf8JsonReader(utf8, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("word"u8)) { reader.Read(); e.Word = ReadStringValue(ref reader) ?? string.Empty; }
            else if (reader.ValueTextEquals("lang_code"u8)) { reader.Read(); langCode = ReadStringValue(ref reader); }
            else if (reader.ValueTextEquals("lang"u8)) { reader.Read(); lang = ReadStringValue(ref reader); }
            else if (reader.ValueTextEquals("pos"u8)) { reader.Read(); e.Pos = ReadStringValue(ref reader); }
            else if (reader.ValueTextEquals("etymology_text"u8)) { reader.Read(); e.EtymologyText = ReadStringValue(ref reader); }
            else if (reader.ValueTextEquals("senses"u8)) { reader.Read(); e.Senses = ReadSenses(ref reader); }
            else if (reader.ValueTextEquals("sounds"u8)) { reader.Read(); e.Sounds = ReadSounds(ref reader); }
            else if (reader.ValueTextEquals("forms"u8)) { reader.Read(); e.Forms = ReadForms(ref reader); }
            else if (reader.ValueTextEquals("translations"u8))
            {
                reader.Read();
                if (e.IncludeTranslations) e.Translations = ReadWordArray(ref reader);
                else reader.Skip();
            }
            else if (reader.ValueTextEquals("etymology_templates"u8)) { reader.Read(); e.EtymologyTemplates = ReadEtyTemplates(ref reader); }
            else if (reader.ValueTextEquals("synonyms"u8)) { reader.Read(); e.Top.Synonyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("antonyms"u8)) { reader.Read(); e.Top.Antonyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("hyponyms"u8)) { reader.Read(); e.Top.Hyponyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("meronyms"u8)) { reader.Read(); e.Top.Meronyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("holonyms"u8)) { reader.Read(); e.Top.Holonyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("related"u8)) { reader.Read(); e.Top.Related = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("hypernyms"u8)) { reader.Read(); e.Top.Hypernyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("derived"u8)) { reader.Read(); e.Top.Derived = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("coordinate_terms"u8)) { reader.Read(); e.Top.Coordinate = ReadWordArray(ref reader); }
            else reader.Skip();
        }

        if (string.IsNullOrEmpty(e.Word)) return null;

        e.LangCode = langCode ?? lang;
        bool langActive = options.Languages?.IsActive == true;
        if (e.LangCode is { } lc)
        {
            if (langActive && options.Languages!.MatchesRaw(lc) == false) return null;
        }
        else if (langActive)
        {
            return null;
        }

        return e;
    }

    private static List<Sense>? ReadSenses(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
        List<Sense>? senses = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
            var s = ReadSense(ref reader);
            (senses ??= new()).Add(s);
        }
        return senses;
    }

    private static Sense ReadSense(ref Utf8JsonReader reader)
    {
        var s = new Sense();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("glosses"u8)) { reader.Read(); s.Glosses = ReadStringArray(ref reader); }
            else if (reader.ValueTextEquals("examples"u8)) { reader.Read(); s.Examples = ReadExamples(ref reader); }
            else if (reader.ValueTextEquals("synonyms"u8)) { reader.Read(); s.Relations.Synonyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("antonyms"u8)) { reader.Read(); s.Relations.Antonyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("hyponyms"u8)) { reader.Read(); s.Relations.Hyponyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("meronyms"u8)) { reader.Read(); s.Relations.Meronyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("holonyms"u8)) { reader.Read(); s.Relations.Holonyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("related"u8)) { reader.Read(); s.Relations.Related = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("hypernyms"u8)) { reader.Read(); s.Relations.Hypernyms = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("derived"u8)) { reader.Read(); s.Relations.Derived = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("coordinate_terms"u8)) { reader.Read(); s.Relations.Coordinate = ReadWordArray(ref reader); }
            else if (reader.ValueTextEquals("tags"u8)) { reader.Read(); s.Tags = ReadStringArray(ref reader); }
            else if (reader.ValueTextEquals("links"u8)) { reader.Read(); s.LinkTargets = ReadLinks(ref reader); }
            else if (reader.ValueTextEquals("wikidata"u8)) { reader.Read(); s.SynsetKey ??= ReadStringValue(ref reader); }
            else if (reader.ValueTextEquals("senseid"u8))
            {
                reader.Read();
                // wikidata wins; only take senseid when wikidata absent (matches witness OR-order).
                string? sid = ReadStringValue(ref reader);
                s.SynsetKey ??= sid;
            }
            else reader.Skip();
        }
        return s;
    }

    // examples[]: either a bare string, or an object with a "text" field.
    private static List<string>? ReadExamples(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
        List<string>? list = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string? v = reader.GetString();
                if (!string.IsNullOrEmpty(v)) (list ??= new()).Add(v);
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                string? text = ReadNamedString(ref reader, "text"u8);
                if (!string.IsNullOrEmpty(text)) (list ??= new()).Add(text);
            }
            else reader.Skip();
        }
        return list;
    }

    // links[]: array of [label, target] string arrays; the routing key is target ([1]).
    private static List<string>? ReadLinks(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
        List<string>? targets = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); continue; }
            int idx = 0;
            string? target = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    if (idx == 1) target = reader.GetString();
                    idx++;
                }
                else { reader.Skip(); idx++; }
            }
            if (!string.IsNullOrEmpty(target)) (targets ??= new()).Add(target);
        }
        return targets;
    }

    private static List<Sound>? ReadSounds(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
        List<Sound>? sounds = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
            string? ipa = null;
            List<string>? tags = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("ipa"u8)) { reader.Read(); ipa = ReadStringValue(ref reader); }
                else if (reader.ValueTextEquals("tags"u8)) { reader.Read(); tags = ReadStringArray(ref reader); }
                else reader.Skip();
            }
            if (!string.IsNullOrEmpty(ipa)) (sounds ??= new()).Add(new Sound(ipa, tags));
        }
        return sounds;
    }

    private static List<Form>? ReadForms(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
        List<Form>? forms = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
            string? form = null;
            List<string>? tags = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("form"u8)) { reader.Read(); form = ReadStringValue(ref reader); }
                else if (reader.ValueTextEquals("tags"u8)) { reader.Read(); tags = ReadStringArray(ref reader); }
                else reader.Skip();
            }
            if (!string.IsNullOrEmpty(form)) (forms ??= new()).Add(new Form(form, tags));
        }
        return forms;
    }

    private static List<EtyTemplate>? ReadEtyTemplates(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
        List<EtyTemplate>? list = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
            var t = new EtyTemplate();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("name"u8)) { reader.Read(); t.Name = ReadStringValue(ref reader); }
                else if (reader.ValueTextEquals("args"u8)) { reader.Read(); t.Args = ReadStringMap(ref reader); }
                else reader.Skip();
            }
            (list ??= new()).Add(t);
        }
        return list;
    }

    private static Dictionary<string, string>? ReadStringMap(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return null; }
        Dictionary<string, string>? map = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string key = reader.GetString() ?? string.Empty;
            reader.Read();
            if (reader.TokenType == JsonTokenType.String)
            {
                string? v = reader.GetString();
                if (v is not null) (map ??= new()).Add(key, v);
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }
        return map;
    }

    // Object-array carrying a "word" field per element (synonyms, hypernyms, …).
    private static List<string>? ReadWordArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
        List<string>? list = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
            string? w = ReadNamedString(ref reader, "word"u8);
            if (!string.IsNullOrEmpty(w)) (list ??= new()).Add(w);
        }
        return list;
    }

    private static List<string>? ReadStringArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) { reader.Skip(); return null; }
        List<string>? list = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string? v = reader.GetString();
                if (!string.IsNullOrEmpty(v)) (list ??= new()).Add(v);
            }
            else reader.Skip();
        }
        return list;
    }

    // Read the first-level string property `name` from the object the reader is
    // positioned at (StartObject), skipping everything else.
    private static string? ReadNamedString(ref Utf8JsonReader reader, ReadOnlySpan<byte> name)
    {
        string? found = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (found is null && reader.ValueTextEquals(name))
            {
                reader.Read();
                found = ReadStringValue(ref reader);
            }
            else
            {
                reader.Read();
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    reader.Skip();
            }
        }
        return found;
    }

    // The reader is positioned ON the value token. Returns the string for String
    // tokens; skips containers and yields null for anything else.
    private static string? ReadStringValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }
}
