using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Laplace.Engine.Core;
using Laplace.Native;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// Deterministic native geometric trace, Fréchet curve comparison, and UCD <c>tier0.flags</c>
/// metadata inspection backed by <c>Laplace.Native</c> and <c>Laplace.Engine.Core</c>.
/// Replaces the removed legacy <c>WorkspaceCodeIndex</c> prototype.
/// </summary>
internal static class GeometricTextScorer
{
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
        IReadOnlyDictionary<Hash128, double> WordWeights);

    internal sealed record ScoreResult(
        double RelevanceScore,
        double FrechetDistance,
        double CentroidDistance,
        double WordOverlap,
        int SharedWords);

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

        using var tree = TextDecomposer.Run(text);
        HashComposer.Run(tree, &ResolvePerfcache);

        var unitIdx = tree.NaturalUnitIndex();
        var root = tree.GetNode(unitIdx);

        double cx = root.Coord[0];
        double cy = root.Coord[1];
        double cz = root.Coord[2];
        double cm = root.Coord[3];
        double radius = Math.Sqrt(cx * cx + cy * cy + cz * cz + cm * cm);

        var wordList = new List<(uint Off, Hash128 Id, double X, double Y, double Z, double M)>();
        var weights = new Dictionary<Hash128, double>();
        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var n = tree.GetNode(i);
            if (n.Tier != EntityTiers.Word) continue;
            var id = n.Id;
            wordList.Add((n.TextRangeOff, id, n.Coord[0], n.Coord[1], n.Coord[2], n.Coord[3]));
            weights[id] = weights.TryGetValue(id, out var prev) ? prev + 1.0 : 1.0;
        }
        wordList.Sort(static (a, b) => a.Off.CompareTo(b.Off));

        if (wordList.Count == 0)
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
                weights);
        }

        var trajectory = new double[wordList.Count * 4];
        var wordIds = new Hash128[wordList.Count];
        for (int i = 0; i < wordList.Count; i++)
        {
            wordIds[i] = wordList[i].Id;
            trajectory[i * 4] = wordList[i].X;
            trajectory[i * 4 + 1] = wordList[i].Y;
            trajectory[i * 4 + 2] = wordList[i].Z;
            trajectory[i * 4 + 3] = wordList[i].M;
        }

        return new Trace(
            root.Id,
            Convert.ToHexStringLower(root.Id.ToBytes()),
            cx, cy, cz, cm,
            radius,
            trajectory,
            wordIds,
            weights);
    }

    public static ScoreResult ScoreCandidate(
        Trace query,
        Trace candidate,
        IReadOnlyDictionary<Hash128, Hash128[]>? semanticPeers = null)
    {
        var qSet = query.WordIds.ToHashSet();
        var dSet = candidate.WordIds.ToHashSet();

        int directShared = 0;
        double weightedHits = 0.0;
        double queryTotal = Math.Max(1.0, query.WordWeights.Values.Sum());

        foreach (var (qid, weight) in query.WordWeights)
        {
            if (dSet.Contains(qid))
            {
                directShared++;
                weightedHits += weight;
                continue;
            }
            if (semanticPeers is not null && semanticPeers.TryGetValue(qid, out var peers))
            {
                foreach (var peer in peers)
                {
                    if (dSet.Contains(peer))
                    {
                        weightedHits += weight * 0.65;
                        break;
                    }
                }
            }
        }

        double overlap = Math.Clamp(weightedHits / queryTotal, 0.0, 1.0);
        double dx = query.Cx - candidate.Cx;
        double dy = query.Cy - candidate.Cy;
        double dz = query.Cz - candidate.Cz;
        double dm = query.Cm - candidate.Cm;
        double centroidDist = Math.Sqrt(dx * dx + dy * dy + dz * dz + dm * dm);

        double frechetDist = DiscreteFrechet4d(query.Trajectory, candidate.Trajectory);
        double geomScore = 1.0 / (1.0 + centroidDist);
        double curveScore = 1.0 / (1.0 + frechetDist);

        double score = overlap * 0.60 + curveScore * 0.25 + geomScore * 0.15;
        return new ScoreResult(score, frechetDist, centroidDist, overlap, directShared);
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
            // Managed Unicode fallback when laplace native library is unavailable.
        }

        if (gc is null && Rune.TryCreate(codepoint, out var rune))
            gc = ShortCategoryCode(Rune.GetUnicodeCategory(rune));

        return new CodepointFlagFacet(gc, sc, hex);
    }

    private static double DiscreteFrechet4d(double[] a, double[] b)
    {
        int na = a.Length / 4;
        int nb = b.Length / 4;
        if (na == 0 || nb == 0) return 2.0;

        // Bound quadratic DP cost on long inputs.
        na = Math.Min(na, 64);
        nb = Math.Min(nb, 64);

        var dp = new double[na, nb];
        dp[0, 0] = Dist4(a, 0, b, 0);
        for (int i = 1; i < na; i++)
            dp[i, 0] = Math.Max(dp[i - 1, 0], Dist4(a, i, b, 0));
        for (int j = 1; j < nb; j++)
            dp[0, j] = Math.Max(dp[0, j - 1], Dist4(a, 0, b, j));

        for (int i = 1; i < na; i++)
        {
            for (int j = 1; j < nb; j++)
            {
                double prev = Math.Min(dp[i - 1, j], Math.Min(dp[i - 1, j - 1], dp[i, j - 1]));
                dp[i, j] = Math.Max(prev, Dist4(a, i, b, j));
            }
        }
        return dp[na - 1, nb - 1];
    }

    private static double Dist4(double[] a, int i, double[] b, int j)
    {
        int ia = i * 4;
        int ib = j * 4;
        double d0 = a[ia] - b[ib];
        double d1 = a[ia + 1] - b[ib + 1];
        double d2 = a[ia + 2] - b[ib + 2];
        double d3 = a[ia + 3] - b[ib + 3];
        return Math.Sqrt(d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3);
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
