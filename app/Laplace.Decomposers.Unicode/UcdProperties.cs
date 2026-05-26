using System.Globalization;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Unicode;

/// <summary>
/// Parsed UCD text-file properties needed for attestation emission by
/// <see cref="UnicodeDecomposer"/>. Loads three files under
/// <c>{ucdDir}/</c>: <c>UnicodeData.txt</c>, <c>Scripts.txt</c>,
/// <c>Blocks.txt</c>. All data is load-once; field arrays are indexed by
/// codepoint scalar value (max 0x10FFFF). Range-based properties (script,
/// block) are stored as sorted range arrays with binary-search lookup.
/// </summary>
internal sealed class UcdProperties
{
    // ── attestation-kind IDs (content-addressed, stable) ──

    public static readonly Hash128 KindHasGeneralCategory  = Hash128.OfCanonical("substrate/kind/HAS_GENERAL_CATEGORY/v1");
    public static readonly Hash128 KindHasCombiningClass   = Hash128.OfCanonical("substrate/kind/HAS_COMBINING_CLASS/v1");
    public static readonly Hash128 KindHasScript           = Hash128.OfCanonical("substrate/kind/HAS_SCRIPT/v1");
    public static readonly Hash128 KindHasBlock            = Hash128.OfCanonical("substrate/kind/HAS_BLOCK/v1");
    public static readonly Hash128 KindHasUppercaseMapping = Hash128.OfCanonical("substrate/kind/HAS_UPPERCASE_MAPPING/v1");
    public static readonly Hash128 KindHasLowercaseMapping = Hash128.OfCanonical("substrate/kind/HAS_LOWERCASE_MAPPING/v1");
    public static readonly Hash128 KindCanonDecomposesTo   = Hash128.OfCanonical("substrate/kind/CANONICAL_DECOMPOSES_TO/v1");

    // ── per-codepoint arrays (length = 0x110000) ──

    /// <summary>Two-letter general category string per codepoint, or null
    /// for unassigned (Cn).</summary>
    public readonly string?[] GeneralCategory;

    /// <summary>Canonical combining class (0–254) per codepoint.</summary>
    public readonly byte[] CombiningClass;

    /// <summary>Uppercase mapping target, or 0 if none.</summary>
    public readonly uint[] UppercaseMapping;

    /// <summary>Lowercase mapping target, or 0 if none.</summary>
    public readonly uint[] LowercaseMapping;

    /// <summary>Canonical decomposition targets per codepoint (null if none;
    /// 1 or 2 targets for most canonical mappings). Compatibility
    /// decompositions (with &lt;tag&gt;) are excluded.</summary>
    public readonly uint[]?[] CanonDecomp;

    // ── range arrays for script + block ──

    private readonly (uint S, uint E, string N)[] _scriptRanges;
    private readonly (uint S, uint E, string N)[] _blockRanges;

    // ── entity ID caches for classification objects ──

    public readonly Dictionary<string, Hash128> CategoryEntityIds;
    public readonly Dictionary<string, Hash128> ScriptEntityIds;
    public readonly Dictionary<string, Hash128> BlockEntityIds;

    // ── ordinal context IDs for decomp sequences ──
    public static readonly Hash128 OrdinalCtx0 = Hash128.OfCanonical("ordinal/0/v1");
    public static readonly Hash128 OrdinalCtx1 = Hash128.OfCanonical("ordinal/1/v1");

    private UcdProperties(
        string?[] generalCategory, byte[] combiningClass,
        uint[] uppercaseMapping, uint[] lowercaseMapping,
        uint[]?[] canonDecomp,
        (uint S, uint E, string N)[] scriptRanges,
        (uint S, uint E, string N)[] blockRanges)
    {
        GeneralCategory  = generalCategory;
        CombiningClass   = combiningClass;
        UppercaseMapping = uppercaseMapping;
        LowercaseMapping = lowercaseMapping;
        CanonDecomp      = canonDecomp;
        _scriptRanges    = scriptRanges;
        _blockRanges     = blockRanges;

        CategoryEntityIds = BuildEntityIds(generalCategory.Where(x => x != null).Distinct()!,
                                           "unicode/category/{0}/v1");
        ScriptEntityIds   = BuildEntityIds(scriptRanges.Select(r => r.N).Distinct(),
                                           "unicode/script/{0}/v1");
        BlockEntityIds    = BuildEntityIds(blockRanges.Select(r => r.N).Distinct(),
                                           "unicode/block/{0}/v1");
    }

    private static Dictionary<string, Hash128> BuildEntityIds(
        IEnumerable<string> names, string fmtTemplate)
    {
        var d = new Dictionary<string, Hash128>(StringComparer.Ordinal);
        foreach (var n in names)
            d[n] = Hash128.OfCanonical(string.Format(fmtTemplate, n));
        return d;
    }

    // ── lookup helpers ──

    public string? ScriptForCodepoint(uint cp)  => RangeLookup(_scriptRanges, cp);
    public string? BlockForCodepoint(uint cp)   => RangeLookup(_blockRanges, cp);

    private static string? RangeLookup((uint S, uint E, string N)[] ranges, uint cp)
    {
        int lo = 0, hi = ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var (s, e, n) = ranges[mid];
            if (cp < s) hi = mid - 1;
            else if (cp > e) lo = mid + 1;
            else return n;
        }
        return null;
    }

    // ── bootstrap entity rows for classification objects ──

    /// <summary>Entity rows for all category / script / block classifier
    /// entities. Emitted in InitializeAsync so BuildBatch can reference
    /// them without re-emitting.</summary>
    public IEnumerable<EntityRow> ClassificationEntities(Hash128 sourceId)
    {
        var typeId = Hash128.OfCanonical("substrate/type/UcdClassifier/v1");
        foreach (var (_, id) in CategoryEntityIds)
            yield return new EntityRow(id, 0, typeId, sourceId);
        foreach (var (_, id) in ScriptEntityIds)
            yield return new EntityRow(id, 0, typeId, sourceId);
        foreach (var (_, id) in BlockEntityIds)
            yield return new EntityRow(id, 0, typeId, sourceId);
    }

    // ── factory ──

    public static UcdProperties Load(string ucdDir)
    {
        const int Total = 0x110000;
        var genCat   = new string?[Total];
        var combCls  = new byte[Total];
        var upperMap = new uint[Total];
        var lowerMap = new uint[Total];
        var canonDec = new uint[]?[Total];

        // ── UnicodeData.txt ──
        string udPath = Path.Combine(ucdDir, "UnicodeData.txt");
        Span<Range> ranges = stackalloc Range[15];
        foreach (var line in File.ReadLines(udPath))
        {
            if (string.IsNullOrEmpty(line)) continue;
            var span = line.AsSpan();
            int n = span.Split(ranges, ';');
            if (n < 5) continue;

            if (!uint.TryParse(span[ranges[0]], NumberStyles.HexNumber, null, out uint cp)) continue;
            if (cp >= Total) continue;

            genCat[cp] = string.Intern(new string(span[ranges[2]]));

            if (byte.TryParse(span[ranges[3]], out byte cc))
                combCls[cp] = cc;

            // Field 5: decomposition mapping
            var decompSpan = span[ranges[5]];
            if (!decompSpan.IsEmpty)
            {
                // Skip if compatibility (starts with '<')
                if (decompSpan[0] != '<')
                {
                    // Parse space-separated hex codepoints
                    var targets = new List<uint>(2);
                    foreach (var part in decompSpan.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (uint.TryParse(part, NumberStyles.HexNumber, null, out uint t) && t < Total)
                            targets.Add(t);
                    }
                    if (targets.Count > 0)
                        canonDec[cp] = targets.ToArray();
                }
            }

            // Field 12: uppercase mapping
            if (n > 12 && !span[ranges[12]].IsEmpty)
            {
                if (uint.TryParse(span[ranges[12]], NumberStyles.HexNumber, null, out uint u))
                    upperMap[cp] = u;
            }

            // Field 13: lowercase mapping
            if (n > 13 && !span[ranges[13]].IsEmpty)
            {
                if (uint.TryParse(span[ranges[13]], NumberStyles.HexNumber, null, out uint l))
                    lowerMap[cp] = l;
            }
        }

        // ── Scripts.txt ──
        var scriptRanges = ParseRangeFile(Path.Combine(ucdDir, "Scripts.txt"));

        // ── Blocks.txt ──
        var blockRanges = ParseRangeFile(Path.Combine(ucdDir, "Blocks.txt"));

        return new UcdProperties(genCat, combCls, upperMap, lowerMap, canonDec, scriptRanges, blockRanges);
    }

    private static (uint S, uint E, string N)[] ParseRangeFile(string path)
    {
        var list = new List<(uint S, uint E, string N)>();
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.AsSpan();
            int comment = trimmed.IndexOf('#');
            if (comment >= 0) trimmed = trimmed.Slice(0, comment);
            trimmed = trimmed.Trim();
            if (trimmed.IsEmpty) continue;

            int semi = trimmed.IndexOf(';');
            if (semi < 0) continue;
            var rangePart = trimmed.Slice(0, semi).Trim();
            var namePart  = trimmed.Slice(semi + 1).Trim();
            string name   = namePart.ToString().Trim();

            int dotdot = rangePart.IndexOf("..");
            uint start, end;
            if (dotdot >= 0)
            {
                if (!uint.TryParse(rangePart.Slice(0, dotdot), NumberStyles.HexNumber, null, out start)) continue;
                if (!uint.TryParse(rangePart.Slice(dotdot + 2), NumberStyles.HexNumber, null, out end)) continue;
            }
            else
            {
                if (!uint.TryParse(rangePart, NumberStyles.HexNumber, null, out start)) continue;
                end = start;
            }
            if (start >= 0x110000u) continue;
            end = Math.Min(end, 0x10FFFFu);
            if (!string.IsNullOrEmpty(name))
                list.Add((start, end, string.Intern(name)));
        }
        list.Sort((a, b) => a.S.CompareTo(b.S));
        return list.ToArray();
    }
}
