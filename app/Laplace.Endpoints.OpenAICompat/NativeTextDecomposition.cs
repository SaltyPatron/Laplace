using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;
using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Thin managed wrapper over <c>Laplace.Native</c> / <c>Laplace.Engine.Core</c> for
/// deterministic text decomposition, 4D coordinate resolution, native Fréchet/distance
/// metrics (<c>lp_frechet4</c>, <c>lp_distance4</c>), and <c>tier0.flags</c> inspection.
/// </summary>
internal static class NativeTextDecomposition
{
    private const int MaxFrechetPoints = 64;

    internal sealed record Trace(
        Hash128 RootId,
        string RootIdHex,
        double Cx,
        double Cy,
        double Cz,
        double Cm,
        double Radius,
        double[] Trajectory,
        Hash128[] WordIds,
        IReadOnlyDictionary<Hash128, int> WordCounts);

    internal sealed record CodepointFlagFacet(
        string? GeneralCategory,
        string? Script,
        string? FlagsHex);

    private static readonly object FlagsGate = new();
    private static unsafe lp_layout* _flagsLayout;
    private static unsafe lp_field* _gcField;
    private static unsafe lp_field* _scField;
    private static bool _flagsInitialized;

    public static unsafe Trace ComputeTrace(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        CodepointPerfcache.LoadDefault();

        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        using var tree = TextDecomposer.Run(utf8);
        HashComposer.Run(tree, &ResolvePerfcache);

        var unitIdx = tree.NaturalUnitIndex();
        var root = tree.GetNode(unitIdx);

        double cx = root.Coord[0];
        double cy = root.Coord[1];
        double cz = root.Coord[2];
        double cm = root.Coord[3];
        double radius = Math.Sqrt(cx * cx + cy * cy + cz * cz + cm * cm);

        var nodeCoords = new Dictionary<Hash128, (double X, double Y, double Z, double M)>(tree.NodeCount);
        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var n = tree.GetNode(i);
            nodeCoords.TryAdd(n.Id, (n.Coord[0], n.Coord[1], n.Coord[2], n.Coord[3]));
        }

        // Native UAX #29 segments: keep word segments holding a letter or digit
        // (tier is bottom-up composition depth and does not select words; see TextDecomposer.WordSegments).
        var allSegments = TextDecomposer.WordSegments(utf8);
        var wordSegments = new List<TextDecomposer.WordSegment>(allSegments.Length);
        foreach (var seg in allSegments)
        {
            if (HasLetterOrDigit(utf8.AsSpan(seg.Offset, seg.Length)))
                wordSegments.Add(seg);
        }

        if (wordSegments.Count == 0 && allSegments.Length > 0)
            wordSegments.AddRange(allSegments);

        if (wordSegments.Count == 0)
        {
            var atoms = new List<(uint Off, double X, double Y, double Z, double M)>();
            for (uint i = 0; i < tree.NodeCount; i++)
            {
                var n = tree.GetNode(i);
                if (n.Tier != EntityTiers.Codepoint) continue;
                atoms.Add((n.TextRangeOff, n.Coord[0], n.Coord[1], n.Coord[2], n.Coord[3]));
            }
            atoms.Sort(static (a, b) => a.Off.CompareTo(b.Off));
            var trajAtoms = new double[atoms.Count * 4];
            for (int i = 0; i < atoms.Count; i++)
            {
                trajAtoms[i * 4] = atoms[i].X;
                trajAtoms[i * 4 + 1] = atoms[i].Y;
                trajAtoms[i * 4 + 2] = atoms[i].Z;
                trajAtoms[i * 4 + 3] = atoms[i].M;
            }
            return new Trace(
                root.Id,
                Convert.ToHexStringLower(root.Id.ToBytes()),
                cx, cy, cz, cm,
                radius,
                trajAtoms,
                Array.Empty<Hash128>(),
                new Dictionary<Hash128, int>());
        }

        var trajectory = new double[wordSegments.Count * 4];
        var orderedWordIds = new List<Hash128>(wordSegments.Count);
        var counts = new Dictionary<Hash128, int>(wordSegments.Count);
        for (int i = 0; i < wordSegments.Count; i++)
        {
            var id = wordSegments[i].Id;
            orderedWordIds.Add(id);
            counts[id] = counts.TryGetValue(id, out var prev) ? prev + 1 : 1;
            if (nodeCoords.TryGetValue(id, out var pt))
            {
                trajectory[i * 4] = pt.X;
                trajectory[i * 4 + 1] = pt.Y;
                trajectory[i * 4 + 2] = pt.Z;
                trajectory[i * 4 + 3] = pt.M;
            }
        }

        return new Trace(
            root.Id,
            Convert.ToHexStringLower(root.Id.ToBytes()),
            cx, cy, cz, cm,
            radius,
            trajectory,
            orderedWordIds.ToArray(),
            counts);
    }

    private static bool HasLetterOrDigit(ReadOnlySpan<byte> utf8)
    {
        int i = 0;
        while (i < utf8.Length)
        {
            if (Rune.DecodeFromUtf8(utf8[i..], out var rune, out int consumed) != System.Buffers.OperationStatus.Done || consumed <= 0)
                return false;
            if (Rune.IsLetterOrDigit(rune))
                return true;
            i += consumed;
        }
        return false;
    }

    public static double NativeFrechet4d(double[] a, double[] b)
    {
        int lenA = Math.Min(a.Length - (a.Length % 4), MaxFrechetPoints * 4);
        int lenB = Math.Min(b.Length - (b.Length % 4), MaxFrechetPoints * 4);
        if (lenA == 0 || lenB == 0) return 2.0;
        return Math4d.Frechet(a.AsSpan(0, lenA), b.AsSpan(0, lenB));
    }

    public static double NativeDistance4d(Trace a, Trace b)
    {
        ReadOnlySpan<double> pa = [a.Cx, a.Cy, a.Cz, a.Cm];
        ReadOnlySpan<double> pb = [b.Cx, b.Cy, b.Cz, b.Cm];
        return Math4d.Distance(pa, pb);
    }

    public static CodepointFlagFacet InspectCodepointFlags(uint codepoint)
    {
        string? gc = null;
        string? sc = null;
        string? hex = null;

        try
        {
            unsafe
            {
                EnsureFlagsMapped();
                if (_flagsLayout is not null && codepoint < 1_114_112u)
                {
                    if (_gcField is not null)
                    {
                        uint v = LaplaceNative.lp_flags_get(_flagsLayout, codepoint, _gcField);
                        gc = ReadFlagValueName(_flagsLayout, _gcField, v);
                    }
                    if (_scField is not null)
                    {
                        uint v = LaplaceNative.lp_flags_get(_flagsLayout, codepoint, _scField);
                        sc = ReadFlagValueName(_flagsLayout, _scField, v);
                    }
                    ulong rawWord = 0;
                    var rec = _flagsLayout->flags + codepoint;
                    for (int k = 0; k < 8; k++)
                        rawWord |= (ulong)rec->b[k] << (8 * k);
                    hex = $"0x{rawWord:x16}";
                }
            }
        }
        catch (DllNotFoundException)
        {
            // Managed Unicode fallback only when the native library cannot be loaded.
        }

        if (gc is null && Rune.TryCreate(codepoint, out var rune))
            gc = ShortCategoryCode(Rune.GetUnicodeCategory(rune));

        return new CodepointFlagFacet(gc, sc, hex);
    }

    private static unsafe void EnsureFlagsMapped()
    {
        if (_flagsInitialized) return;
        lock (FlagsGate)
        {
            if (_flagsInitialized) return;
            _flagsInitialized = true;
            var pathPtr = LaplaceNative.lp_flags_path();
            if (pathPtr is null) return;
            _flagsLayout = LaplaceNative.lp_flags_map(pathPtr);
            if (_flagsLayout is null) return;

            byte[] gcName = "gc\0"u8.ToArray();
            byte[] scName = "sc\0"u8.ToArray();
            fixed (byte* gcPtr = gcName)
            fixed (byte* scPtr = scName)
            {
                _gcField = LaplaceNative.lp_flags_field(_flagsLayout, gcPtr);
                _scField = LaplaceNative.lp_flags_field(_flagsLayout, scPtr);
            }
        }
    }

    private static unsafe string? ReadFlagValueName(lp_layout* layout, lp_field* field, uint val)
    {
        if (layout is null || field is null || field->nvalues == 0) return null;
        if (val >= field->nvalues) return null;
        var entry = layout->value + field->first + val;
        int len = 0;
        while (len < 48 && entry->name[len] != 0) len++;
        return len == 0 ? null : Encoding.UTF8.GetString(entry->name, len);
    }

    private static string ShortCategoryCode(UnicodeCategory cat) => cat switch
    {
        UnicodeCategory.UppercaseLetter => "Lu",
        UnicodeCategory.LowercaseLetter => "Ll",
        UnicodeCategory.TitlecaseLetter => "Lt",
        UnicodeCategory.ModifierLetter => "Lm",
        UnicodeCategory.OtherLetter => "Lo",
        UnicodeCategory.NonSpacingMark => "Mn",
        UnicodeCategory.SpacingCombiningMark => "Mc",
        UnicodeCategory.EnclosingMark => "Me",
        UnicodeCategory.DecimalDigitNumber => "Nd",
        UnicodeCategory.LetterNumber => "Nl",
        UnicodeCategory.OtherNumber => "No",
        UnicodeCategory.ConnectorPunctuation => "Pc",
        UnicodeCategory.DashPunctuation => "Pd",
        UnicodeCategory.OpenPunctuation => "Ps",
        UnicodeCategory.ClosePunctuation => "Pe",
        UnicodeCategory.InitialQuotePunctuation => "Pi",
        UnicodeCategory.FinalQuotePunctuation => "Pf",
        UnicodeCategory.OtherPunctuation => "Po",
        UnicodeCategory.MathSymbol => "Sm",
        UnicodeCategory.CurrencySymbol => "Sc",
        UnicodeCategory.ModifierSymbol => "Sk",
        UnicodeCategory.OtherSymbol => "So",
        UnicodeCategory.SpaceSeparator => "Zs",
        UnicodeCategory.LineSeparator => "Zl",
        UnicodeCategory.ParagraphSeparator => "Zp",
        UnicodeCategory.Control => "Cc",
        UnicodeCategory.Format => "Cf",
        UnicodeCategory.Surrogate => "Cs",
        UnicodeCategory.PrivateUse => "Co",
        _ => "Cn"
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ResolvePerfcache(
        uint atom, IntPtr userData, Hash128* outId, double* outCoord, Hilbert128* outHb)
    {
        if (!CodepointPerfcache.TryLookupCodepoint(atom, out var r)) return -1;
        *outId = r.Hash;
        outCoord[0] = r.CoordX;
        outCoord[1] = r.CoordY;
        outCoord[2] = r.CoordZ;
        outCoord[3] = r.CoordM;
        *outHb = r.Hilbert;
        return 0;
    }
}
