using System.Globalization;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Unicode;

internal sealed class UcdProperties
{
    public static readonly Hash128 KindHasGeneralCategory  = RelationTypeRegistry.RelationTypeId("HAS_GENERAL_CATEGORY");
    public static readonly Hash128 KindHasCombiningClass   = RelationTypeRegistry.RelationTypeId("HAS_COMBINING_CLASS");
    public static readonly Hash128 KindHasScript           = RelationTypeRegistry.RelationTypeId("HAS_SCRIPT");
    public static readonly Hash128 KindHasBlock            = RelationTypeRegistry.RelationTypeId("HAS_BLOCK");
    public static readonly Hash128 KindHasUppercaseMapping = RelationTypeRegistry.RelationTypeId("HAS_UPPERCASE_MAPPING");
    public static readonly Hash128 KindHasLowercaseMapping = RelationTypeRegistry.RelationTypeId("HAS_LOWERCASE_MAPPING");
    public static readonly Hash128 KindHasTitlecaseMapping = RelationTypeRegistry.RelationTypeId("HAS_TITLECASE_MAPPING");
    public static readonly Hash128 KindCanonDecomposesTo   = RelationTypeRegistry.RelationTypeId("CANONICAL_DECOMPOSES_TO");
    public static readonly Hash128 KindCompatDecomposesTo  = RelationTypeRegistry.RelationTypeId("COMPATIBILITY_DECOMPOSES_TO");
    public static readonly Hash128 KindHasNumericValue     = RelationTypeRegistry.RelationTypeId("HAS_NUMERIC_VALUE");
    public static readonly Hash128 KindHasBidiClass        = RelationTypeRegistry.RelationTypeId("HAS_BIDI_CLASS");
    public static readonly Hash128 KindHasMirror           = RelationTypeRegistry.RelationTypeId("HAS_MIRROR");
    public static readonly Hash128 KindHasAge               = RelationTypeRegistry.RelationTypeId("HAS_AGE");
    public static readonly Hash128 KindHasNameAlias         = RelationTypeRegistry.RelationTypeId("HAS_NAME_ALIAS");
    public static readonly Hash128 KindConfusableWith       = RelationTypeRegistry.RelationTypeId("CONFUSABLE_WITH");
    public static readonly Hash128 KindHasEmojiProperty     = RelationTypeRegistry.RelationTypeId("HAS_EMOJI_PROPERTY");

    public readonly string?[] GeneralCategory;

    public readonly byte[] CombiningClass;

    public readonly uint[] UppercaseMapping;

    public readonly uint[] LowercaseMapping;

    public readonly uint[] TitlecaseMapping;

    public readonly string?[] NumericValue;

    public readonly string?[] BidiClass;

    public readonly uint[] BidiMirror;

    public readonly byte[] EmojiProps;
    public static readonly string[] EmojiPropNames =
        ["Emoji", "Emoji_Presentation", "Emoji_Modifier", "Emoji_Modifier_Base",
         "Emoji_Component", "Extended_Pictographic"];

    public readonly Dictionary<uint, List<string>> NameAliases;

    public readonly List<(uint Src, string Target)> Confusables;

    public readonly uint[]?[] CanonDecomp;

    public readonly uint[]?[] CompatDecomp;

    private readonly (uint S, uint E, string N)[] _scriptRanges;
    private readonly (uint S, uint E, string N)[] _blockRanges;
    private readonly (uint S, uint E, string N)[] _ageRanges;

    public readonly Dictionary<string, Hash128> CategoryEntityIds;
    public readonly Dictionary<string, Hash128> ScriptEntityIds;
    public readonly Dictionary<string, Hash128> BlockEntityIds;
    public readonly Dictionary<string, Hash128> BidiClassEntityIds;
    public readonly Dictionary<string, Hash128> AgeEntityIds;
    public readonly Dictionary<string, Hash128> EmojiPropEntityIds;
    public readonly Dictionary<string, Hash128> NumericEntityIds;

    public static readonly Hash128 OrdinalCtx0 = Hash128.OfCanonical("ordinal/0/v1");
    public static readonly Hash128 OrdinalCtx1 = Hash128.OfCanonical("ordinal/1/v1");

    private UcdProperties(
        string?[] generalCategory, byte[] combiningClass,
        uint[] uppercaseMapping, uint[] lowercaseMapping, uint[] titlecaseMapping,
        uint[]?[] canonDecomp, uint[]?[] compatDecomp,
        string?[] numericValue, string?[] bidiClass, uint[] bidiMirror,
        byte[] emojiProps,
        Dictionary<uint, List<string>> nameAliases,
        List<(uint Src, string Target)> confusables,
        (uint S, uint E, string N)[] scriptRanges,
        (uint S, uint E, string N)[] blockRanges,
        (uint S, uint E, string N)[] ageRanges)
    {
        GeneralCategory  = generalCategory;
        CombiningClass   = combiningClass;
        UppercaseMapping = uppercaseMapping;
        LowercaseMapping = lowercaseMapping;
        TitlecaseMapping = titlecaseMapping;
        CanonDecomp      = canonDecomp;
        CompatDecomp     = compatDecomp;
        NumericValue     = numericValue;
        BidiClass        = bidiClass;
        BidiMirror       = bidiMirror;
        EmojiProps       = emojiProps;
        NameAliases      = nameAliases;
        Confusables      = confusables;
        _scriptRanges    = scriptRanges;
        _blockRanges     = blockRanges;
        _ageRanges       = ageRanges;

        CategoryEntityIds = BuildEntityIds(generalCategory.Where(x => x != null).Distinct()!,
                                           "unicode/category/{0}/v1");
        ScriptEntityIds   = BuildEntityIds(scriptRanges.Select(r => r.N).Distinct(),
                                           "unicode/script/{0}/v1");
        BlockEntityIds    = BuildEntityIds(blockRanges.Select(r => r.N).Distinct(),
                                           "unicode/block/{0}/v1");
        BidiClassEntityIds = BuildEntityIds(bidiClass.Where(x => x != null).Distinct()!,
                                           "unicode/bidi_class/{0}/v1");
        AgeEntityIds      = BuildEntityIds(ageRanges.Select(r => r.N).Distinct(),
                                           "unicode/age/{0}/v1");
        EmojiPropEntityIds = BuildEntityIds(EmojiPropNames,
                                           "unicode/emoji/{0}/v1");
        NumericEntityIds  = BuildEntityIds(numericValue.Where(x => x != null).Distinct()!,
                                           "unicode/numeric/{0}/v1");
    }

    private static Dictionary<string, Hash128> BuildEntityIds(
        IEnumerable<string> names, string fmtTemplate)
    {
        var d = new Dictionary<string, Hash128>(StringComparer.Ordinal);
        foreach (var n in names)
            d[n] = Hash128.OfCanonical(string.Format(fmtTemplate, n));
        return d;
    }

    public string? ScriptForCodepoint(uint cp)  => RangeLookup(_scriptRanges, cp);
    public string? BlockForCodepoint(uint cp)   => RangeLookup(_blockRanges, cp);
    public string? AgeForCodepoint(uint cp)     => RangeLookup(_ageRanges, cp);

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

    public IEnumerable<EntityRow> ClassificationEntities(Hash128 sourceId)
    {
        var typeId = Hash128.OfCanonical("substrate/type/UcdClassifier/v1");
        foreach (var (_, id) in CategoryEntityIds)
            yield return new EntityRow(id, EntityTier.Vocabulary, typeId, sourceId);
        foreach (var (_, id) in ScriptEntityIds)
            yield return new EntityRow(id, EntityTier.Vocabulary, typeId, sourceId);
        foreach (var (_, id) in BlockEntityIds)
            yield return new EntityRow(id, EntityTier.Vocabulary, typeId, sourceId);
        foreach (var (_, id) in BidiClassEntityIds)
            yield return new EntityRow(id, EntityTier.Vocabulary, typeId, sourceId);
        foreach (var (_, id) in AgeEntityIds)
            yield return new EntityRow(id, EntityTier.Vocabulary, typeId, sourceId);
        foreach (var (_, id) in EmojiPropEntityIds)
            yield return new EntityRow(id, EntityTier.Vocabulary, typeId, sourceId);
        foreach (var (_, id) in NumericEntityIds)
            yield return new EntityRow(id, EntityTier.Vocabulary, typeId, sourceId);
    }

    public static UcdProperties Load(string ucdDir)
    {
        const int Total = 0x110000;
        var genCat   = new string?[Total];
        var combCls  = new byte[Total];
        var upperMap = new uint[Total];
        var lowerMap = new uint[Total];
        var titleMap = new uint[Total];
        var canonDec = new uint[]?[Total];
        var compatDec = new uint[]?[Total];
        var numeric  = new string?[Total];
        var bidiCls  = new string?[Total];
        var bidiMir  = new uint[Total];
        var emoji    = new byte[Total];

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

            if (!span[ranges[4]].IsEmpty)
                bidiCls[cp] = string.Intern(new string(span[ranges[4]]));

            var decompSpan = span[ranges[5]];
            if (!decompSpan.IsEmpty)
            {
                bool compat = decompSpan[0] == '<';
                var raw = decompSpan.ToString();
                if (compat)
                {
                    int close = raw.IndexOf('>');
                    raw = close >= 0 ? raw[(close + 1)..] : "";
                }
                var targets = new List<uint>(2);
                foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (uint.TryParse(part, NumberStyles.HexNumber, null, out uint t) && t < Total)
                        targets.Add(t);
                }
                if (targets.Count > 0)
                {
                    if (compat) compatDec[cp] = targets.ToArray();
                    else        canonDec[cp]  = targets.ToArray();
                }
            }

            if (n > 8 && !span[ranges[8]].IsEmpty)
                numeric[cp] = string.Intern(new string(span[ranges[8]]));

            if (n > 12 && !span[ranges[12]].IsEmpty)
            {
                if (uint.TryParse(span[ranges[12]], NumberStyles.HexNumber, null, out uint u))
                    upperMap[cp] = u;
            }

            if (n > 13 && !span[ranges[13]].IsEmpty)
            {
                if (uint.TryParse(span[ranges[13]], NumberStyles.HexNumber, null, out uint l))
                    lowerMap[cp] = l;
            }

            if (n > 14 && !span[ranges[14]].IsEmpty)
            {
                if (uint.TryParse(span[ranges[14]], NumberStyles.HexNumber, null, out uint t))
                    titleMap[cp] = t;
            }
        }

        var scriptRanges = ParseRangeFile(Path.Combine(ucdDir, "Scripts.txt"));
        var blockRanges  = ParseRangeFile(Path.Combine(ucdDir, "Blocks.txt"));
        var ageRanges    = File.Exists(Path.Combine(ucdDir, "DerivedAge.txt"))
            ? ParseRangeFile(Path.Combine(ucdDir, "DerivedAge.txt"))
            : Array.Empty<(uint, uint, string)>();

        string mirPath = Path.Combine(ucdDir, "BidiMirroring.txt");
        if (File.Exists(mirPath))
            foreach (var raw in File.ReadLines(mirPath))
            {
                var t = StripComment(raw);
                int semi = t.IndexOf(';');
                if (semi <= 0) continue;
                if (uint.TryParse(t[..semi].Trim(), NumberStyles.HexNumber, null, out uint a)
                    && uint.TryParse(t[(semi + 1)..].Trim(), NumberStyles.HexNumber, null, out uint m)
                    && a < Total && m < Total)
                    bidiMir[a] = m;
            }

        string emojiPath = Path.Combine(ucdDir, "emoji", "emoji-data.txt");
        if (File.Exists(emojiPath))
        {
            var propBit = new Dictionary<string, byte>(StringComparer.Ordinal);
            for (int i = 0; i < EmojiPropNames.Length; i++) propBit[EmojiPropNames[i]] = (byte)(1 << i);
            foreach (var (st, en, name) in ParseRangeFile(emojiPath))
                if (propBit.TryGetValue(name, out byte bit))
                    for (uint c = st; c <= en && c < Total; c++) emoji[c] |= bit;
        }

        var aliases = new Dictionary<uint, List<string>>();
        string aliasPath = Path.Combine(ucdDir, "NameAliases.txt");
        if (File.Exists(aliasPath))
            foreach (var raw in File.ReadLines(aliasPath))
            {
                var t = StripComment(raw);
                var parts = t.Split(';');
                if (parts.Length < 2) continue;
                if (!uint.TryParse(parts[0].Trim(), NumberStyles.HexNumber, null, out uint c) || c >= Total) continue;
                string alias = parts[1].Trim();
                if (alias.Length == 0) continue;
                if (!aliases.TryGetValue(c, out var list)) aliases[c] = list = new List<string>(2);
                list.Add(alias);
            }

        var confusables = new List<(uint, string)>();
        string confPath = Path.Combine(ucdDir, "..", "security", "confusables.txt");
        if (File.Exists(confPath))
            foreach (var raw in File.ReadLines(confPath))
            {
                var t = StripComment(raw);
                var parts = t.Split(';');
                if (parts.Length < 2) continue;
                if (!uint.TryParse(parts[0].Trim(), NumberStyles.HexNumber, null, out uint src) || src >= Total) continue;
                var targetCps = new List<uint>(3);
                bool ok = true;
                foreach (var hx in parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (uint.TryParse(hx, NumberStyles.HexNumber, null, out uint tc) && tc < Total) targetCps.Add(tc);
                    else { ok = false; break; }
                }
                if (!ok || targetCps.Count == 0) continue;
                var sb = new System.Text.StringBuilder(targetCps.Count * 2);
                foreach (var tc in targetCps)
                {
                    if (tc is >= 0xD800 and <= 0xDFFF) { ok = false; break; }
                    sb.Append(char.ConvertFromUtf32((int)tc));
                }
                if (ok) confusables.Add((src, sb.ToString()));
            }

        return new UcdProperties(
            genCat, combCls, upperMap, lowerMap, titleMap,
            canonDec, compatDec, numeric, bidiCls, bidiMir, emoji,
            aliases, confusables,
            scriptRanges, blockRanges, ageRanges);
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOf('#');
        return (hash >= 0 ? line[..hash] : line).Trim();
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
