namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
/// Emits the codepoint name string pool (codepoint_names.{h,c}) — flat
/// null-terminated UTF-8 concatenation indexed by per-codepoint offsets.
/// Returns the offset map so codepoint_table emission can store the offset
/// per codepoint.
/// </summary>
public static class NamePoolEmitter
{
    /// <returns>Map from codepoint integer to byte offset into the pool;
    /// codepoints with no name are not present in the map.</returns>
    public static IReadOnlyDictionary<int, uint> Emit(
        IReadOnlyList<CodepointEntry> entries,
        string outputDir)
    {
        var offsets = new Dictionary<int, uint>(entries.Count);
        var pool    = new List<byte>(capacity: entries.Count * 16);

        // First byte reserved so offset 0 means "no name".
        pool.Add(0);

        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Name))
            {
                continue;
            }
            var offset = (uint) pool.Count;
            offsets[e.Codepoint] = offset;
            pool.AddRange(Encoding.UTF8.GetBytes(e.Name));
            pool.Add(0);
        }

        EmitHeader(pool.Count, outputDir);
        EmitSource(pool, outputDir);
        return offsets;
    }

    private static void EmitHeader(int byteCount, string outputDir)
    {
        var path = Path.Combine(outputDir, "codepoint_names.h");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#ifndef LAPLACE_CODEPOINT_NAMES_H");
        w.WriteLine("#define LAPLACE_CODEPOINT_NAMES_H");
        w.WriteLine();
        w.WriteLine("#include <stdint.h>");
        w.WriteLine();
        w.Write("#define LAPLACE_CODEPOINT_NAME_POOL_BYTES ");
        w.WriteLine(byteCount.ToString(CultureInfo.InvariantCulture));
        w.WriteLine();
        w.WriteLine("extern const char LAPLACE_CODEPOINT_NAME_POOL[LAPLACE_CODEPOINT_NAME_POOL_BYTES];");
        w.WriteLine();
        w.WriteLine("/* Returns the null-terminated UTF-8 name for `cp`, or the empty string");
        w.WriteLine(" * if no name is recorded. Caller must NOT free. */");
        w.WriteLine("const char *laplace_codepoint_name(int32_t cp);");
        w.WriteLine();
        w.WriteLine("#endif /* LAPLACE_CODEPOINT_NAMES_H */");
    }

    private static void EmitSource(List<byte> pool, string outputDir)
    {
        var path = Path.Combine(outputDir, "codepoint_names.c");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#include \"laplace_pg/codepoint_names.h\"");
        w.WriteLine("#include \"laplace_pg/codepoint_table.h\"");
        w.WriteLine();
        w.WriteLine("const char LAPLACE_CODEPOINT_NAME_POOL[LAPLACE_CODEPOINT_NAME_POOL_BYTES] = {");
        const int perLine = 16;
        for (int i = 0; i < pool.Count; ++i)
        {
            if (i % perLine == 0) { w.Write("    "); }
            w.Write("0x");
            w.Write(pool[i].ToString("X2", CultureInfo.InvariantCulture));
            w.Write(',');
            if ((i + 1) % perLine == 0) { w.WriteLine(); }
        }
        if (pool.Count % perLine != 0) { w.WriteLine(); }
        w.WriteLine("};");
        w.WriteLine();
        w.WriteLine("/* Per-codepoint name offset is stored in the codepoint_table-aware");
        w.WriteLine(" * extended properties table; this lookup defers to that table. */");
        w.WriteLine("extern uint32_t laplace_codepoint_name_offset(int32_t cp);");
        w.WriteLine();
        w.WriteLine("const char *laplace_codepoint_name(int32_t cp)");
        w.WriteLine("{");
        w.WriteLine("    uint32_t offset = laplace_codepoint_name_offset(cp);");
        w.WriteLine("    if (offset == 0u) { return \"\"; }");
        w.WriteLine("    return &LAPLACE_CODEPOINT_NAME_POOL[offset];");
        w.WriteLine("}");
    }
}
