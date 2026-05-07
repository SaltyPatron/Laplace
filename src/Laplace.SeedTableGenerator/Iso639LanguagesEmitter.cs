namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Laplace.Core.Abstractions;

/// <summary>
/// Emits iso639_languages.{h,c}: per ISO 639-3 language, the
/// content-addressed entity_hash for the language entity (computed as a
/// tier-1 composition of the alpha-3 code's codepoint LINESTRING) plus the
/// ISO 639-3 attributes the substrate decomposers need to attest.
///
/// Per substrate invariant 1, languages are entities — referenced
/// everywhere by entity_hash, never by integer language_id. Decomposers
/// for per-language seeds (Wiktionary fr.json, UD English treebank, etc.)
/// look up the language's entity_hash here when emitting "this entity is
/// in language X" edges.
/// </summary>
public static class Iso639LanguagesEmitter
{
    public static void Emit(
        IReadOnlyList<Decomposers.Iso639.Iso639LanguageRecord> records,
        IReadOnlyDictionary<int, AtomId> codepointHashes,
        IIdentityHashing hashing,
        string outputDir)
    {
        var entries = new List<(string code, AtomId hash, Decomposers.Iso639.Iso639LanguageRecord rec)>(records.Count);
        foreach (var r in records)
        {
            if (string.IsNullOrEmpty(r.Id))
            {
                continue;
            }
            var hash = ComposeLanguageEntity(r.Id, codepointHashes, hashing);
            entries.Add((r.Id, hash, r));
        }

        EmitHeader(entries.Count, outputDir);
        EmitSource(entries, outputDir);
    }

    private static AtomId ComposeLanguageEntity(
        string isoCode,
        IReadOnlyDictionary<int, AtomId> codepointHashes,
        IIdentityHashing hashing)
    {
        // The language entity is the substrate composition of its alpha-3
        // code's codepoint LINESTRING. "eng" = composition over (h_e, h_n, h_g).
        var children = new List<AtomId>(isoCode.Length);
        var counts   = new List<int>(isoCode.Length);
        foreach (var ch in isoCode)
        {
            if (codepointHashes.TryGetValue(ch, out var hash))
            {
                children.Add(hash);
                counts.Add(1);
            }
        }
        return hashing.CompositionId(children, counts);
    }

    private static void EmitHeader(int count, string outputDir)
    {
        var path = Path.Combine(outputDir, "iso639_languages.h");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#ifndef LAPLACE_ISO639_LANGUAGES_H");
        w.WriteLine("#define LAPLACE_ISO639_LANGUAGES_H");
        w.WriteLine();
        w.WriteLine("#include <stdint.h>");
        w.WriteLine();
        w.Write("#define LAPLACE_ISO639_COUNT ");
        w.WriteLine(count.ToString(CultureInfo.InvariantCulture));
        w.WriteLine();
        w.WriteLine("typedef struct {");
        w.WriteLine("    char     iso_alpha3[4];        /* ISO 639-3 alpha-3 + null terminator */");
        w.WriteLine("    uint8_t  hash[32];             /* substrate entity_hash for this language */");
        w.WriteLine("    uint8_t  scope;                /* 0=Individual 1=Macrolanguage 2=Special */");
        w.WriteLine("    uint8_t  type;                 /* 0=Ancient 1=Constructed 2=Extinct 3=Historical 4=Living 5=Special */");
        w.WriteLine("} laplace_iso639_entry_t;");
        w.WriteLine();
        w.WriteLine("extern const laplace_iso639_entry_t LAPLACE_ISO639[LAPLACE_ISO639_COUNT];");
        w.WriteLine();
        w.WriteLine("#endif /* LAPLACE_ISO639_LANGUAGES_H */");
    }

    private static void EmitSource(
        List<(string code, AtomId hash, Decomposers.Iso639.Iso639LanguageRecord rec)> entries,
        string outputDir)
    {
        var path = Path.Combine(outputDir, "iso639_languages.c");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#include \"laplace_pg/iso639_languages.h\"");
        w.WriteLine();
        w.WriteLine("const laplace_iso639_entry_t LAPLACE_ISO639[LAPLACE_ISO639_COUNT] = {");
        var sb = new StringBuilder(160);
        foreach (var (code, hash, rec) in entries)
        {
            sb.Clear();
            sb.Append("    {\"");
            sb.Append(code);
            sb.Append("\",");
            sb.Append(CHeaderWriter.FormatHashInit(hash.AsSpan()));
            sb.Append(',');
            sb.Append(((int) rec.Scope).ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(((int) rec.Type).ToString(CultureInfo.InvariantCulture));
            sb.Append("},");
            w.WriteLine(sb.ToString());
        }
        w.WriteLine("};");
    }
}
