namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Laplace.Decomposers.Ucd;

/// <summary>
/// Emits the UCA weight table — codepoint integer → (primary, secondary,
/// tertiary) collation weights from DUCET (allkeys.txt). Sparse: only
/// codepoints with a UCA entry appear; @implicitweights ranges expand to
/// per-codepoint entries via the parser.
///
/// Used by F1 + decomposers that need UCA-weighted comparison and at
/// generator time as one of the canonical-ordering keys for tier-0 atom
/// super-Fibonacci placement.
/// </summary>
public static class UcaWeightsEmitter
{
    public static void Emit(IEnumerable<UcaEntry> ucaEntries, string outputDir)
    {
        var byCodepoint = new SortedDictionary<int, (ushort p, ushort s, ushort t)>();
        foreach (var entry in ucaEntries)
        {
            if (entry.SourceCodepoints.Count != 1)
            {
                // Multi-codepoint contractions are not stored per-codepoint;
                // the substrate's recomposer / collation queries handle them
                // separately via the entity composition graph.
                continue;
            }
            var element = entry.Elements.Count > 0
                ? entry.Elements[0]
                : new UcaCollationElement(false, 0, 0, 0);
            byCodepoint[entry.SourceCodepoints[0]] = (element.Primary, element.Secondary, element.Tertiary);
        }

        EmitHeader(byCodepoint.Count, outputDir);
        EmitSource(byCodepoint, outputDir);
    }

    private static void EmitHeader(int count, string outputDir)
    {
        var path = Path.Combine(outputDir, "uca_weights.h");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#ifndef LAPLACE_UCA_WEIGHTS_H");
        w.WriteLine("#define LAPLACE_UCA_WEIGHTS_H");
        w.WriteLine();
        w.WriteLine("#include <stdint.h>");
        w.WriteLine();
        w.Write("#define LAPLACE_UCA_WEIGHTS_COUNT ");
        w.WriteLine(count.ToString(CultureInfo.InvariantCulture));
        w.WriteLine();
        w.WriteLine("typedef struct {");
        w.WriteLine("    int32_t  codepoint;");
        w.WriteLine("    uint16_t primary;");
        w.WriteLine("    uint16_t secondary;");
        w.WriteLine("    uint16_t tertiary;");
        w.WriteLine("} laplace_uca_weight_entry_t;");
        w.WriteLine();
        w.WriteLine("extern const laplace_uca_weight_entry_t");
        w.WriteLine("    LAPLACE_UCA_WEIGHTS[LAPLACE_UCA_WEIGHTS_COUNT];");
        w.WriteLine();
        w.WriteLine("#endif /* LAPLACE_UCA_WEIGHTS_H */");
    }

    private static void EmitSource(
        SortedDictionary<int, (ushort p, ushort s, ushort t)> byCodepoint,
        string outputDir)
    {
        var path = Path.Combine(outputDir, "uca_weights.c");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#include \"laplace_pg/uca_weights.h\"");
        w.WriteLine();
        w.WriteLine("const laplace_uca_weight_entry_t");
        w.WriteLine("    LAPLACE_UCA_WEIGHTS[LAPLACE_UCA_WEIGHTS_COUNT] = {");
        foreach (var (cp, (p, s, t)) in byCodepoint)
        {
            w.Write("    {");
            w.Write(cp.ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(p.ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(s.ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(t.ToString(CultureInfo.InvariantCulture));
            w.WriteLine("},");
        }
        w.WriteLine("};");
    }
}
