namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Emits the substrate's compiled per-codepoint C table — the load-bearing
/// acceleration artifact for tier-0 atom lookups. Decomposers (Track F)
/// read this in-process for microsecond `codepoint → entity_hash` and
/// `codepoint → S³ position` access without round-tripping the database.
///
/// Output:
///   codepoint_table.h — struct definition + extern array declaration
///   codepoint_table.c — array contents (one entry per ordered codepoint)
///
/// Auxiliary tables (names, decompositions, registries, UCA weights,
/// emoji sequences, ISO 639 languages) are emitted by separate emitters
/// and reference offsets/indices into shared pools.
/// </summary>
public static class CodepointTableEmitter
{
    public static void Emit(IReadOnlyList<CodepointEntry> entries, string outputDir)
    {
        EmitHeader(outputDir, entries.Count);
        EmitSource(entries, outputDir);
    }

    private static void EmitHeader(string outputDir, int count)
    {
        var path = Path.Combine(outputDir, "codepoint_table.h");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#ifndef LAPLACE_CODEPOINT_TABLE_H");
        w.WriteLine("#define LAPLACE_CODEPOINT_TABLE_H");
        w.WriteLine();
        w.WriteLine("#include <stdint.h>");
        w.WriteLine();
        w.Write("#define LAPLACE_CODEPOINT_TABLE_COUNT ");
        w.WriteLine(count.ToString(CultureInfo.InvariantCulture));
        w.WriteLine();
        w.WriteLine("typedef struct {");
        w.WriteLine("    int32_t  codepoint;            /* canonical Unicode codepoint integer */");
        w.WriteLine("    uint8_t  hash[32];             /* BLAKE3 entity_hash of UTF-8 bytes */");
        w.WriteLine("    double   s3[4];                /* canonical super-Fibonacci position */");
        w.WriteLine("    uint64_t hilbert_index;        /* 64-bit Hilbert linearization key */");
        w.WriteLine("    uint64_t prime_flags;          /* OR-combinable categorical bitmask */");
        w.WriteLine("} laplace_codepoint_entry_t;");
        w.WriteLine();
        w.WriteLine("extern const laplace_codepoint_entry_t");
        w.WriteLine("    LAPLACE_CODEPOINT_TABLE[LAPLACE_CODEPOINT_TABLE_COUNT];");
        w.WriteLine();
        w.WriteLine("#endif /* LAPLACE_CODEPOINT_TABLE_H */");
    }

    private static void EmitSource(IReadOnlyList<CodepointEntry> entries, string outputDir)
    {
        var path = Path.Combine(outputDir, "codepoint_table.c");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#include \"laplace_pg/codepoint_table.h\"");
        w.WriteLine();
        w.WriteLine("const laplace_codepoint_entry_t");
        w.WriteLine("    LAPLACE_CODEPOINT_TABLE[LAPLACE_CODEPOINT_TABLE_COUNT] = {");

        for (int i = 0; i < entries.Count; ++i)
        {
            var e = entries[i];
            w.Write("    {");
            w.Write(e.Codepoint.ToString(CultureInfo.InvariantCulture));
            w.Write(",");
            w.Write(CHeaderWriter.FormatHashInit(e.EntityHash.AsSpan()));
            w.Write(",{");
            w.Write(CHeaderWriter.FormatDouble(e.Position.X)); w.Write(',');
            w.Write(CHeaderWriter.FormatDouble(e.Position.Y)); w.Write(',');
            w.Write(CHeaderWriter.FormatDouble(e.Position.Z)); w.Write(',');
            w.Write(CHeaderWriter.FormatDouble(e.Position.W));
            w.Write("},0x");
            w.Write(e.HilbertIndex.ToString("X16", CultureInfo.InvariantCulture));
            w.Write("ULL,0x");
            w.Write(e.PrimeFlags.ToString("X16", CultureInfo.InvariantCulture));
            w.WriteLine("ULL},");
        }

        w.WriteLine("};");
    }
}
