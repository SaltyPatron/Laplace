namespace Laplace.SeedTableGenerator;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Laplace.Decomposers.Ucd;

/// <summary>
/// Emits the codepoint decomposition table — sparse (~7K codepoints have
/// non-trivial decomposition out of 1.114M). Used by NFC/NFD normalization
/// in F1 TextDecomposer + B13 UnicodeIcuService verification, and as
/// edges sourced from UCD for the substrate's decomposition_of relation.
/// </summary>
public static class DecompositionTableEmitter
{
    public static void Emit(IReadOnlyList<UcdCodepointRecord> records, string outputDir)
    {
        var entries = new List<(int cp, string? type, IReadOnlyList<int> targets)>();
        foreach (var r in records)
        {
            for (int cp = r.FirstCodepoint; cp <= r.LastCodepoint; ++cp)
            {
                var dm = r.DecompositionMapping;
                if (string.IsNullOrEmpty(dm) || dm == "#")
                {
                    continue;
                }
                var targets = ParseHexSequence(dm);
                if (targets.Count == 0 || (targets.Count == 1 && targets[0] == cp))
                {
                    continue;
                }
                var dt = r.DecompositionType;
                entries.Add((cp, string.IsNullOrEmpty(dt) || dt == "none" ? null : dt, targets));
            }
        }

        EmitHeader(entries.Count, outputDir);
        EmitSource(entries, outputDir);
    }

    private static List<int> ParseHexSequence(string s)
    {
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<int>(tokens.Length);
        foreach (var t in tokens)
        {
            if (int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
            {
                result.Add(cp);
            }
        }
        return result;
    }

    private static void EmitHeader(int count, string outputDir)
    {
        var path = Path.Combine(outputDir, "codepoint_decompositions.h");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#ifndef LAPLACE_CODEPOINT_DECOMPOSITIONS_H");
        w.WriteLine("#define LAPLACE_CODEPOINT_DECOMPOSITIONS_H");
        w.WriteLine();
        w.WriteLine("#include <stdint.h>");
        w.WriteLine();
        w.Write("#define LAPLACE_DECOMPOSITION_COUNT ");
        w.WriteLine(count.ToString(CultureInfo.InvariantCulture));
        w.WriteLine();
        w.WriteLine("typedef struct {");
        w.WriteLine("    int32_t  source_codepoint;");
        w.WriteLine("    uint8_t  type_tag;       /* 0=canonical, 1=compat, 2=font, 3=noBreak,");
        w.WriteLine("                              * 4=initial, 5=medial, 6=final, 7=isolated,");
        w.WriteLine("                              * 8=circle, 9=super, 10=sub, 11=vertical,");
        w.WriteLine("                              * 12=wide, 13=narrow, 14=small, 15=square,");
        w.WriteLine("                              * 16=fraction, 17=other, 255=none */");
        w.WriteLine("    uint16_t target_offset;  /* offset into LAPLACE_DECOMPOSITION_TARGETS */");
        w.WriteLine("    uint8_t  target_count;");
        w.WriteLine("} laplace_decomposition_entry_t;");
        w.WriteLine();
        w.WriteLine("extern const laplace_decomposition_entry_t");
        w.WriteLine("    LAPLACE_DECOMPOSITIONS[LAPLACE_DECOMPOSITION_COUNT];");
        w.WriteLine("extern const int32_t LAPLACE_DECOMPOSITION_TARGETS[];");
        w.WriteLine();
        w.WriteLine("#endif /* LAPLACE_CODEPOINT_DECOMPOSITIONS_H */");
    }

    private static void EmitSource(
        List<(int cp, string? type, IReadOnlyList<int> targets)> entries,
        string outputDir)
    {
        var path = Path.Combine(outputDir, "codepoint_decompositions.c");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#include \"laplace_pg/codepoint_decompositions.h\"");
        w.WriteLine();

        var flat = new List<int>();
        var offsets = new List<int>(entries.Count);
        foreach (var (_, _, targets) in entries)
        {
            offsets.Add(flat.Count);
            flat.AddRange(targets);
        }

        w.WriteLine("const int32_t LAPLACE_DECOMPOSITION_TARGETS[] = {");
        for (int i = 0; i < flat.Count; ++i)
        {
            if (i % 8 == 0) { w.Write("    "); }
            w.Write(flat[i].ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            if ((i + 1) % 8 == 0) { w.WriteLine(); }
        }
        if (flat.Count % 8 != 0) { w.WriteLine(); }
        w.WriteLine("};");
        w.WriteLine();
        w.WriteLine("const laplace_decomposition_entry_t");
        w.WriteLine("    LAPLACE_DECOMPOSITIONS[LAPLACE_DECOMPOSITION_COUNT] = {");
        for (int i = 0; i < entries.Count; ++i)
        {
            var (cp, type, targets) = entries[i];
            w.Write("    {");
            w.Write(cp.ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(EncodeTypeTag(type).ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(offsets[i].ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(targets.Count.ToString(CultureInfo.InvariantCulture));
            w.WriteLine("},");
        }
        w.WriteLine("};");
    }

    private static byte EncodeTypeTag(string? type)
    {
        if (string.IsNullOrEmpty(type)) { return 0; } // canonical
        return type switch
        {
            "<font>"     => 2,
            "<noBreak>"  => 3,
            "<initial>"  => 4,
            "<medial>"   => 5,
            "<final>"    => 6,
            "<isolated>" => 7,
            "<circle>"   => 8,
            "<super>"    => 9,
            "<sub>"      => 10,
            "<vertical>" => 11,
            "<wide>"     => 12,
            "<narrow>"   => 13,
            "<small>"    => 14,
            "<square>"   => 15,
            "<fraction>" => 16,
            "<compat>"   => 1,
            _            => 17,
        };
    }
}
