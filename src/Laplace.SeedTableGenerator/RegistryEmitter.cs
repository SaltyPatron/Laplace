namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Laplace.Core.Abstractions;

/// <summary>
/// Generic emitter for the per-codepoint UCD enum-name registries
/// (script, block, age, general_category, bidi_class). Each registry maps
/// an enum index → (canonical_name_string, substrate_entity_hash).
///
/// Per substrate invariant 1: the enum index is an in-process cache key
/// for fast UCD-property dispatch; the canonical IDENTITY of each registry
/// value (e.g., script "Latn") is its substrate entity_hash, computed as
/// the tier-1 composition of its name's codepoint LINESTRING.
/// </summary>
public static class RegistryEmitter
{
    public static void Emit(
        string symbolBase,
        IEnumerable<string> distinctNames,
        IReadOnlyDictionary<int, AtomId> codepointHashes,
        IIdentityHashing hashing,
        string outputDir)
    {
        var sorted = new List<string>();
        var seen   = new HashSet<string>();
        foreach (var n in distinctNames)
        {
            if (string.IsNullOrEmpty(n) || !seen.Add(n)) { continue; }
            sorted.Add(n);
        }
        sorted.Sort(System.StringComparer.Ordinal);

        var entries = new (string Name, AtomId Hash)[sorted.Count];
        for (int i = 0; i < sorted.Count; ++i)
        {
            entries[i] = (sorted[i], ComposeFromName(sorted[i], codepointHashes, hashing));
        }

        EmitHeader(symbolBase, entries.Length, outputDir);
        EmitSource(symbolBase, entries, outputDir);
    }

    private static AtomId ComposeFromName(
        string name,
        IReadOnlyDictionary<int, AtomId> codepointHashes,
        IIdentityHashing hashing)
    {
        var children = new List<AtomId>(name.Length);
        var counts   = new List<int>(name.Length);
        foreach (var ch in name)
        {
            if (codepointHashes.TryGetValue(ch, out var hash))
            {
                children.Add(hash);
                counts.Add(1);
            }
        }
        return hashing.CompositionId(children, counts);
    }

    private static void EmitHeader(string symbolBase, int count, string outputDir)
    {
        var path = Path.Combine(outputDir, $"{symbolBase}_registry.h");
        var symbolUpper = symbolBase.ToUpperInvariant();
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine($"#ifndef LAPLACE_{symbolUpper}_REGISTRY_H");
        w.WriteLine($"#define LAPLACE_{symbolUpper}_REGISTRY_H");
        w.WriteLine();
        w.WriteLine("#include <stdint.h>");
        w.WriteLine();
        w.Write($"#define LAPLACE_{symbolUpper}_REGISTRY_COUNT ");
        w.WriteLine(count.ToString(CultureInfo.InvariantCulture));
        w.WriteLine();
        w.WriteLine("typedef struct {");
        w.WriteLine("    const char *name;     /* canonical UCD short code */");
        w.WriteLine("    uint8_t     hash[32]; /* substrate entity_hash for this value */");
        w.WriteLine($"}} laplace_{symbolBase}_registry_entry_t;");
        w.WriteLine();
        w.WriteLine($"extern const laplace_{symbolBase}_registry_entry_t");
        w.WriteLine($"    LAPLACE_{symbolUpper}_REGISTRY[LAPLACE_{symbolUpper}_REGISTRY_COUNT];");
        w.WriteLine();
        w.WriteLine($"#endif /* LAPLACE_{symbolUpper}_REGISTRY_H */");
    }

    private static void EmitSource(
        string symbolBase,
        (string Name, AtomId Hash)[] entries,
        string outputDir)
    {
        var path = Path.Combine(outputDir, $"{symbolBase}_registry.c");
        var symbolUpper = symbolBase.ToUpperInvariant();
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine($"#include \"laplace_pg/{symbolBase}_registry.h\"");
        w.WriteLine();
        w.WriteLine($"const laplace_{symbolBase}_registry_entry_t");
        w.WriteLine($"    LAPLACE_{symbolUpper}_REGISTRY[LAPLACE_{symbolUpper}_REGISTRY_COUNT] = {{");
        foreach (var (name, hash) in entries)
        {
            w.Write("    {\"");
            w.Write(EscapeC(name));
            w.Write("\",");
            w.Write(CHeaderWriter.FormatHashInit(hash.AsSpan()));
            w.WriteLine("},");
        }
        w.WriteLine("};");
    }

    private static string EscapeC(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                default:
                    if (c >= 0x20 && c < 0x7F) { sb.Append(c); }
                    else { sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture)); }
                    break;
            }
        }
        return sb.ToString();
    }
}
