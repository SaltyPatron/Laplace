using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using System.Text;

namespace Laplace.Decomposers.Unicode;

/// <summary>
/// Unicode 17 UAX #42 flat-XML lowering.  The XML provider recovers concrete
/// elements/attributes; this recipe assigns every recovered field a typed semantic
/// disposition.  PropertyAliases canonicalizes short and long spellings before a
/// UCD dynamic relation is minted.
/// </summary>
internal sealed class UcdXmlRecipe
{
    internal readonly record struct StructuredCodepointReference(
        uint Codepoint,
        string? Qualifier);

    private static readonly HashSet<string> IdentityAttributes =
        new(["cp", "first-cp", "last-cp"], StringComparer.Ordinal);

    private static readonly HashSet<string> BinaryAttributes = new(
        ("AHex Alpha Bidi_C Bidi_M CE CI CWCF CWCM CWKCF CWL CWT CWU Cased Comp_Ex DI Dash Dep Dia "
        + "EBase EComp EMod EPres Emoji EqUIdeo Ext ExtPict Gr_Base Gr_Ext Hex IDC IDS IDSB IDST IDSU "
        + "ID_Compat_Math_Continue ID_Compat_Math_Start Ideo Join_C LOE Lower MCM Math NChar OAlpha ODI "
        + "OGr_Ext OIDC OIDS OLower OMath OUpper PCM Pat_Syn Pat_WS QMark RI Radical SD STerm Term UIdeo "
        + "Upper VS WSpace XIDC XIDS UnihanCore2020 kEH_Core kEH_NoMirror kEH_NoRotate")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    private static readonly HashSet<string> CodepointAttributes = new(
        "bmg bpb suc slc stc uc lc tc kCompatibilityVariant"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    private static readonly HashSet<string> SequenceAttributes = new(
        "dm scf cf NFKC_CF NFKC_SCF"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    private static readonly HashSet<string> EnumeratedSequenceAttributes =
        new(["scx"], StringComparer.Ordinal);

    private static readonly HashSet<string> StructuredReferenceAttributes = new(
        ("kSemanticVariant kSimplifiedVariant kSpecializedSemanticVariant kSpoofingVariant "
        + "kTraditionalVariant kZVariant")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    private static readonly HashSet<string> IntegerAttributes =
        new(["ccc"], StringComparer.Ordinal);

    private static readonly HashSet<string> NumericAttributes = new(
        "nv kAccountingNumeric kOtherNumeric kPrimaryNumeric kTayNumeric kVietnameseNumeric kZhuangNumeric"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    private static readonly HashSet<string> TextAttributes = new(
        "na na1 JSN kDefinition"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    // Exact attribute inventory observed in Unicode 17.0.0 ucd.all.flat.xml.  A
    // release cannot activate if the provider observes a field absent from this set.
    internal static readonly string[] RepertoireAttributes =
        ("AHex Alpha Bidi_C Bidi_M CE CI CWCF CWCM CWKCF CWL CWT CWU Cased Comp_Ex DI Dash Dep Dia "
        + "EBase EComp EMod EPres Emoji EqUIdeo Ext ExtPict GCB Gr_Base Gr_Ext Hex IDC IDS IDSB IDST IDSU "
        + "ID_Compat_Math_Continue ID_Compat_Math_Start Ideo InCB InPC InSC JSN Join_C LOE Lower MCM Math "
        + "NChar NFC_QC NFD_QC NFKC_CF NFKC_QC NFKC_SCF NFKD_QC OAlpha ODI OGr_Ext OIDC OIDS OLower "
        + "OMath OUpper PCM Pat_Syn Pat_WS QMark RI Radical SB SD STerm Term UIdeo Upper VS WB WSpace XIDC "
        + "XIDS age bc blk bmg bpb bpt ccc cf cp dm dt ea first-cp gc hst jg jt "
        + "kAccountingNumeric kAlternateTotalStrokes kBigFive kCCCII kCNS1986 kCNS1992 kCangjie kCantonese "
        + "kCheungBauer kCheungBauerIndex kCihaiT kCompatibilityVariant kCowles kDaeJaweon kDefinition kEACC "
        + "kEH_AltSeq kEH_Cat kEH_Core kEH_Desc kEH_FVal kEH_Func kEH_HG kEH_IFAO kEH_JSesh kEH_NoMirror "
        + "kEH_NoRotate kEH_UniK kFanqie kFenn kFennIndex kFourCornerCode kGB0 kGB1 kGB3 kGB5 kGB8 kGSR "
        + "kGradeLevel kHDZRadBreak kHKGlyph kHanYu kHangul kHanyuPinlu kHanyuPinyin kIBMJapan kIICore "
        + "kIRGDaeJaweon kIRGHanyuDaZidian kIRGKangXi kIRG_GSource kIRG_HSource kIRG_JSource kIRG_KPSource "
        + "kIRG_KSource kIRG_MSource kIRG_SSource kIRG_TSource kIRG_UKSource kIRG_USource kIRG_VSource "
        + "kJIS0213 kJapanese kJapaneseKun kJapaneseOn kJinmeiyoKanji kJis0 kJis1 kJoyoKanji kKangXi "
        + "kKarlgren kKorean kKoreanEducationHanja kKoreanName kLau kMainlandTelegraph kMandarin kMatthews "
        + "kMeyerWempe kMojiJoho kMorohashi kNSHU_DubenSrc kNSHU_Reading kNelson kOtherNumeric kPhonetic "
        + "kPrimaryNumeric kPseudoGB1 kRSAdobe_Japan1_6 kRSUnicode kSBGY kSMSZD2003Index kSMSZD2003Readings "
        + "kSemanticVariant kSimplifiedVariant kSpecializedSemanticVariant kSpoofingVariant kStrange kTGH "
        + "kTGHZ2013 kTGT_MergedSrc kTGT_RSUnicode kTaiwanTelegraph kTang kTayNumeric kTotalStrokes "
        + "kTraditionalVariant kUnihanCore2020 kVietnamese kVietnameseNumeric kXHC1983 kXerox kZVariant "
        + "kZhuang kZhuangNumeric last-cp lb lc na na1 nt nv sc scf scx slc stc suc tc uc vo")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private UcdXmlRecipe(
        SemanticSourceRecipe recipe,
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, string> valueAliases)
    {
        Recipe = recipe;
        Aliases = aliases;
        ValueAliases = valueAliases;
    }

    internal SemanticSourceRecipe Recipe { get; }
    internal IReadOnlyDictionary<string, string> Aliases { get; }
    internal IReadOnlyDictionary<string, string> ValueAliases { get; }

    internal string CanonicalProperty(string alias) =>
        Aliases.TryGetValue(alias, out string? canonical) ? canonical : alias;

    internal string CanonicalValue(string property, string alias)
    {
        property = CanonicalProperty(property);
        if (property == "Script_Extensions") property = "Script";
        return ValueAliases.TryGetValue(ValueKey(property, alias), out string? canonical)
            ? canonical
            : alias;
    }

    internal static IReadOnlyList<StructuredCodepointReference> ParseStructuredReferences(
        string syntaxPath,
        string raw)
    {
        var result = new List<StructuredCodepointReference>();
        foreach (string token in raw.Split(
                     ' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) continue;
            int qualifierAt = token.IndexOf('<');
            string codepointText = qualifierAt < 0
                ? token[2..]
                : token[2..qualifierAt];
            if (!uint.TryParse(
                    codepointText,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out uint codepoint)
                || codepoint > 0x10FFFFu)
                throw new InvalidDataException(
                    $"UCDXML field '{syntaxPath}' has invalid structured reference '{token}'.");
            result.Add(new StructuredCodepointReference(
                codepoint,
                qualifierAt < 0 || qualifierAt + 1 >= token.Length
                    ? null
                    : token[(qualifierAt + 1)..]));
        }

        if (result.Count == 0)
            throw new InvalidDataException(
                $"UCDXML structured reference field '{syntaxPath}' has no U+ target in '{raw}'.");
        return result;
    }

    internal async Task ValidateProviderAsync(
        Stream xml,
        CancellationToken ct = default)
    {
        const string ucdNamespace = "http://www.unicode.org/ns/2003/ucd/1.0";
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var structures = new HashSet<string>(StringComparer.Ordinal);

        await foreach (XmlRecordFrame frame in XmlRecordReader.ReadAsync(
                           xml, recordDepth: 2, ct: ct).ConfigureAwait(false))
        {
            if (frame.Kind != XmlRecordFrameKind.Record) continue;
            XmlRecordNode node = frame.Node;
            if (!node.NamespaceUri.Equals(ucdNamespace, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"UCDXML node '{node.Name}' has unexpected namespace '{node.NamespaceUri}'.");

            (string Catalog, string Structure, string FieldPrefix) route = node.Name switch
            {
                "char" => ("repertoire", "repertoire/char", "repertoire/*"),
                "reserved" => ("repertoire", "repertoire/reserved", "repertoire/*"),
                "noncharacter" => ("repertoire", "repertoire/noncharacter", "repertoire/*"),
                "surrogate" => ("repertoire", "repertoire/surrogate", "repertoire/*"),
                "block" => ("blocks", "blocks", "blocks/block"),
                "named-sequence" =>
                    ("named-sequences", "named-sequences", "named-sequences/named-sequence"),
                "standardized-variant" =>
                    ("standardized-variants", "standardized-variants", "standardized-variants/standardized-variant"),
                "cjk-radical" =>
                    ("cjk-radicals", "cjk-radicals", "cjk-radicals/cjk-radical"),
                "instead" => ("do-not-emit", "do-not-emit", "do-not-emit/instead"),
                _ => throw new InvalidDataException(
                    $"UCDXML provider emitted unaccounted record node '{node.Name}'."),
            };
            structures.Add(route.Catalog);
            structures.Add(route.Structure);

            foreach (XmlRecordAttribute attribute in node.Attributes)
                AccountField($"{route.FieldPrefix}/@{attribute.Name}");

            foreach (XmlRecordNode child in node.Children)
            {
                if (node.Name is not ("char" or "reserved" or "noncharacter" or "surrogate")
                    || child.Name != "name-alias")
                    throw new InvalidDataException(
                        $"UCDXML provider emitted unaccounted nested node '{node.Name}/{child.Name}'.");
                foreach (XmlRecordAttribute attribute in child.Attributes)
                    AccountField($"repertoire/*/name-alias/@{attribute.Name}");
            }
        }

        string[] missingFields = Recipe.Fields
            .Select(static field => field.SyntaxPath)
            .Where(path => !fields.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] missingStructures = Recipe.Structures
            .Select(static structure => structure.SyntaxPath)
            .Where(path => !structures.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missingFields.Length != 0 || missingStructures.Length != 0)
            throw new InvalidDataException(
                "UCDXML provider schema does not satisfy the selected Laplace Recipe; "
                + $"missing fields=[{string.Join(", ", missingFields)}], "
                + $"missing structures=[{string.Join(", ", missingStructures)}].");

        void AccountField(string path)
        {
            if (!Recipe.TryField(path, out _))
                throw new InvalidDataException(
                    $"UCDXML provider emitted field '{path}' with no recipe disposition.");
            fields.Add(path);
        }
    }

    internal static UcdXmlRecipe Load(string propertyAliasesPath)
    {
        IReadOnlyDictionary<string, string> aliases = LoadPropertyAliases(propertyAliasesPath);
        string? directory = Path.GetDirectoryName(propertyAliasesPath);
        string valueAliasesPath = Path.Combine(
            directory ?? string.Empty, "PropertyValueAliases.txt");
        IReadOnlyDictionary<string, string> valueAliases = File.Exists(valueAliasesPath)
            ? LoadPropertyValueAliases(valueAliasesPath, aliases)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var fields = new List<SourceRecipeField>(RepertoireAttributes.Length + 24);
        foreach (string attribute in RepertoireAttributes)
        {
            SourceValueKind kind = ValueKind(attribute);
            SourceFieldDisposition disposition = IdentityAttributes.Contains(attribute)
                ? SourceFieldDisposition.Identity | SourceFieldDisposition.Reference
                : kind is SourceValueKind.Codepoint or SourceValueKind.CodepointSequence
                    ? SourceFieldDisposition.Reference | SourceFieldDisposition.Testimony
                    : StructuredReferenceAttributes.Contains(attribute)
                        ? SourceFieldDisposition.Content | SourceFieldDisposition.Reference
                            | SourceFieldDisposition.Testimony
                    : kind is SourceValueKind.Text or SourceValueKind.StructuredText
                        ? SourceFieldDisposition.Content | SourceFieldDisposition.Testimony
                        : SourceFieldDisposition.Testimony;
            fields.Add(new SourceRecipeField(
                $"repertoire/*/@{attribute}",
                aliases.TryGetValue(attribute, out string? canonical) ? canonical : attribute,
                kind,
                disposition,
                AbsentSentinel: attribute == "nv"
                    ? "NaN"
                    : kind is SourceValueKind.Codepoint or SourceValueKind.CodepointSequence
                        ? "#"
                        : null,
                SequenceSeparator: kind is SourceValueKind.CodepointSequence
                    or SourceValueKind.EnumeratedSequence ? " " : null));
        }

        fields.AddRange(
        [
            F("repertoire/*/name-alias/@alias", "Name_Alias", SourceValueKind.Text,
                SourceFieldDisposition.Content | SourceFieldDisposition.Testimony),
            F("repertoire/*/name-alias/@type", "Name_Alias_Type", SourceValueKind.Enumerated,
                SourceFieldDisposition.Testimony),
            F("blocks/block/@name", "Block", SourceValueKind.Text,
                SourceFieldDisposition.Content | SourceFieldDisposition.Testimony),
            F("blocks/block/@first-cp", "Block_First", SourceValueKind.Codepoint,
                SourceFieldDisposition.Reference | SourceFieldDisposition.Testimony),
            F("blocks/block/@last-cp", "Block_Last", SourceValueKind.Codepoint,
                SourceFieldDisposition.Reference | SourceFieldDisposition.Testimony),
            F("named-sequences/named-sequence/@name", "Named_Sequence_Name", SourceValueKind.Text,
                SourceFieldDisposition.Content | SourceFieldDisposition.Testimony),
            F("named-sequences/named-sequence/@cps", "Named_Sequence", SourceValueKind.CodepointSequence,
                SourceFieldDisposition.Identity | SourceFieldDisposition.Trajectory | SourceFieldDisposition.Reference),
            F("standardized-variants/standardized-variant/@cps", "Standardized_Variant", SourceValueKind.CodepointSequence,
                SourceFieldDisposition.Identity | SourceFieldDisposition.Trajectory | SourceFieldDisposition.Reference),
            F("standardized-variants/standardized-variant/@desc", "Standardized_Variant_Description", SourceValueKind.Text,
                SourceFieldDisposition.Content | SourceFieldDisposition.Testimony),
            F("standardized-variants/standardized-variant/@when", "Standardized_Variant_Condition", SourceValueKind.StructuredText,
                SourceFieldDisposition.Content | SourceFieldDisposition.Testimony),
            F("cjk-radicals/cjk-radical/@number", "CJK_Radical_Number", SourceValueKind.StructuredText,
                SourceFieldDisposition.Identity),
            F("cjk-radicals/cjk-radical/@radical", "CJK_Radical", SourceValueKind.Codepoint,
                SourceFieldDisposition.Reference | SourceFieldDisposition.Testimony),
            F("cjk-radicals/cjk-radical/@ideograph", "CJK_Radical_Ideograph", SourceValueKind.Codepoint,
                SourceFieldDisposition.Reference | SourceFieldDisposition.Testimony),
            F("do-not-emit/instead/@of", "Do_Not_Emit_Source", SourceValueKind.CodepointSequence,
                SourceFieldDisposition.Identity | SourceFieldDisposition.Trajectory | SourceFieldDisposition.Reference),
            F("do-not-emit/instead/@use", "Do_Not_Emit_Replacement", SourceValueKind.CodepointSequence,
                SourceFieldDisposition.Reference | SourceFieldDisposition.Testimony),
            F("do-not-emit/instead/@because", "Do_Not_Emit_Reason", SourceValueKind.Enumerated,
                SourceFieldDisposition.Testimony),
        ]);

        var structures = new[]
        {
            S("repertoire", "Unicode_Repertoire"),
            S("repertoire/char", "Unicode_Character"),
            S("repertoire/reserved", "Unicode_Reserved_Range"),
            S("repertoire/noncharacter", "Unicode_Noncharacter_Range"),
            S("repertoire/surrogate", "Unicode_Surrogate_Range"),
            S("blocks", "Unicode_Block_Catalog"),
            S("named-sequences", "Unicode_Named_Sequence_Catalog"),
            S("standardized-variants", "Unicode_Standardized_Variant_Catalog"),
            S("cjk-radicals", "Unicode_CJK_Radical_Catalog"),
            S("do-not-emit", "Unicode_Do_Not_Emit_Catalog"),
        };

        return new UcdXmlRecipe(
            new SemanticSourceRecipe(
                "Unicode/UCD",
                "17.0.0",
                "laplace/native-streaming-xml-to-universal-ast/v1",
                "UAX42/ucd.all.flat.xml",
                fields,
                structures),
            aliases,
            valueAliases);
    }

    private static SourceRecipeField F(
        string path, string property, SourceValueKind kind, SourceFieldDisposition disposition) =>
        new(path, property, kind, disposition);

    private static SourceRecipeStructure S(string path, string type) =>
        new(path, type,
            SourceFieldDisposition.Identity | SourceFieldDisposition.Trajectory
            | SourceFieldDisposition.Provenance);

    private static SourceValueKind ValueKind(string attribute)
    {
        if (IdentityAttributes.Contains(attribute)) return SourceValueKind.Identity;
        if (BinaryAttributes.Contains(attribute)) return SourceValueKind.Boolean;
        if (CodepointAttributes.Contains(attribute)) return SourceValueKind.Codepoint;
        if (SequenceAttributes.Contains(attribute)) return SourceValueKind.CodepointSequence;
        if (EnumeratedSequenceAttributes.Contains(attribute)) return SourceValueKind.EnumeratedSequence;
        if (IntegerAttributes.Contains(attribute)) return SourceValueKind.Integer;
        if (NumericAttributes.Contains(attribute)) return SourceValueKind.ExactNumber;
        if (TextAttributes.Contains(attribute)) return SourceValueKind.Text;
        return attribute.StartsWith('k') ? SourceValueKind.StructuredText : SourceValueKind.Enumerated;
    }

    private static IReadOnlyDictionary<string, string> LoadPropertyAliases(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw;
            int comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment];
            string[] parts = line.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            string canonical = parts[1];
            foreach (string alias in parts)
                result[alias] = canonical;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> LoadPropertyValueAliases(
        string path,
        IReadOnlyDictionary<string, string> propertyAliases)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw;
            int comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment];
            string[] fields = line.Split(
                ';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3) continue;
            string property = propertyAliases.TryGetValue(fields[0], out string? canonicalProperty)
                ? canonicalProperty
                : fields[0];
            int canonicalIndex = property == "Canonical_Combining_Class" && fields.Length >= 4
                ? 3
                : 2;
            string canonical = fields[canonicalIndex];
            foreach (string alias in fields)
            {
                if (alias == fields[0]) continue;
                result[ValueKey(property, alias)] = canonical;
            }
            result[ValueKey(property, canonical)] = canonical;
        }
        return result;
    }

    private static string ValueKey(string property, string value)
    {
        var normalized = new StringBuilder(value.Length);
        foreach (char c in value)
            if (c is not ('_' or '-' or ' ' or '\t'))
                normalized.Append(char.ToUpperInvariant(c));
        return $"{property}\0{normalized}";
    }
}
