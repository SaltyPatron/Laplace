using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static class RelationTypeRegistry
{
    public enum Symmetry { Asymmetric, Symmetric }

    public readonly record struct RelationTypeResolution(
        Hash128 Id, double Rank, Symmetry Symmetry, bool Flip, Hash128? ParentId, string Canonical);

    private sealed record KindDef(double Rank, Symmetry Symmetry, string? Parent);

    public static Hash128 RelationTypeId(string canonicalName) =>
        Hash128.OfCanonical($"substrate/kind/{canonicalName}/v1");

    private static readonly Dictionary<string, KindDef> Canon = new(StringComparer.Ordinal)
    {
        ["USES_SCRIPT"]              = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_SCRIPT"]              = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_GENERAL_CATEGORY"]    = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_COMBINING_CLASS"]     = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_BLOCK"]               = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_UPPERCASE_MAPPING"]   = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_LOWERCASE_MAPPING"]   = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["CANONICAL_DECOMPOSES_TO"] = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["NORMALIZES_TO"]           = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["TRANSCRIBES_AS"]          = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["DECODES_TO"]    = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_UTF8_ROLE"] = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),

        ["HAS_TITLECASE_MAPPING"]      = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["COMPATIBILITY_DECOMPOSES_TO"] = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_BIDI_CLASS"]             = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_MIRROR"]                 = new(RelationTypeRank.StandardsStructural, Symmetry.Symmetric, null),
        ["HAS_AGE"]                    = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_NAME_ALIAS"]             = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["CONFUSABLE_WITH"]            = new(RelationTypeRank.StandardsStructural, Symmetry.Symmetric, null),
        ["HAS_EMOJI_PROPERTY"]         = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_NUMERIC_VALUE"]          = new(RelationTypeRank.ScalarValued, Symmetry.Asymmetric, null),
        ["HAS_ISO639_2_CODE"]          = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_LANGUAGE_SCOPE"]         = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_LANGUAGE_TYPE"]          = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["IS_LANGUAGE_CODE"]        = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_ISO639_1_CODE"]       = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["MEMBER_OF_MACROLANGUAGE"] = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),
        ["HAS_LANGUAGE"]            = new(RelationTypeRank.StandardsStructural, Symmetry.Asymmetric, null),

        ["IS_A"]           = new(RelationTypeRank.Taxonomic, Symmetry.Asymmetric, null),
        ["IS_INSTANCE_OF"] = new(RelationTypeRank.Taxonomic, Symmetry.Asymmetric, "IS_A"),
        ["MANNER_OF"]      = new(RelationTypeRank.Taxonomic, Symmetry.Asymmetric, "IS_A"),
        ["IS_SENSE_OF"]    = new(RelationTypeRank.Taxonomic, Symmetry.Asymmetric, null),

        ["HAS_PART"]      = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_MEMBER"]    = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),
        ["HAS_SUBSTANCE"] = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),
        ["HAS_ATTRIBUTE"] = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_PROPERTY"]  = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_ATTRIBUTE"),
        ["HAS_A"]         = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),
        ["HAS_POS"]       = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_XPOS"]      = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_POS"),
        ["HAS_FEATURE"]   = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["HAS_SENSE"]     = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["IS_PIXEL_OF"]   = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["IS_AT_SAMPLE"]  = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),

        ["ENTAILS"]            = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["CAUSES"]             = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["CAUSES_DESIRE"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["HAS_SUBEVENT"]       = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["HAS_FIRST_SUBEVENT"] = new(RelationTypeRank.Causal, Symmetry.Asymmetric, "HAS_SUBEVENT"),
        ["HAS_LAST_SUBEVENT"]  = new(RelationTypeRank.Causal, Symmetry.Asymmetric, "HAS_SUBEVENT"),
        ["HAS_PREREQUISITE"]   = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["MOTIVATED_BY_GOAL"]  = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["OBSTRUCTED_BY"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["CREATED_BY"]         = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),

        ["EVOKES_FRAME"]      = new(RelationTypeRank.Taxonomic,  Symmetry.Asymmetric, null),
        ["HAS_FRAME_ELEMENT"] = new(RelationTypeRank.Partitive,  Symmetry.Asymmetric, null),
        ["HAS_THEMATIC_ROLE"] = new(RelationTypeRank.Partitive,  Symmetry.Asymmetric, null),
        ["HAS_SEMANTIC_ROLE"] = new(RelationTypeRank.Partitive,  Symmetry.Asymmetric, null),
        ["FRAME_USES"]        = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "RELATED_TO"),
        ["PERSPECTIVE_ON"]    = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "RELATED_TO"),
        ["CAUSATIVE_OF"]      = new(RelationTypeRank.Causal,     Symmetry.Asymmetric, null),
        ["INCHOATIVE_OF"]     = new(RelationTypeRank.Causal,     Symmetry.Asymmetric, null),
        ["CORRESPONDS_TO"]    = new(RelationTypeRank.Equivalence, Symmetry.Symmetric, null),

        ["X_INTENT"]    = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_NEED"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_WANT"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_EFFECT"]    = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_REACT"]     = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_ATTR"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_REASON"]    = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["X_FILLED_BY"] = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["O_EFFECT"]    = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["O_REACT"]     = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["O_WANT"]      = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["IS_AFTER"]    = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["IS_BEFORE"]   = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["OBJECT_USE"]  = new(RelationTypeRank.Causal, Symmetry.Asymmetric, null),
        ["MADE_UP_OF"]  = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, "HAS_PART"),

        ["DEPENDS_ON"]           = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),
        ["ENHANCED_DEPENDS_ON"]  = new(RelationTypeRank.Partitive, Symmetry.Asymmetric, null),

        ["IS_SYNONYM_OF"]     = new(RelationTypeRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["IS_TRANSLATION_OF"] = new(RelationTypeRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["HAS_VARIANT_OF"]    = new(RelationTypeRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["IS_SIMILAR_TO"]     = new(RelationTypeRank.Equivalence, Symmetry.Symmetric, "RELATED_TO"),
        ["IS_LEMMA_OF"]       = new(RelationTypeRank.Equivalence, Symmetry.Asymmetric, null),
        ["IS_PARTICIPLE_OF"]  = new(RelationTypeRank.Equivalence, Symmetry.Asymmetric, null),
        ["FORM_OF"]           = new(RelationTypeRank.Equivalence, Symmetry.Asymmetric, "RELATED_TO"),

        ["IS_ANTONYM_OF"]    = new(RelationTypeRank.Oppositional, Symmetry.Symmetric, null),
        ["DISTINCT_FROM"]    = new(RelationTypeRank.Oppositional, Symmetry.Symmetric, null),
        ["NOT_DESIRES"]      = new(RelationTypeRank.Oppositional, Symmetry.Asymmetric, null),
        ["NOT_USED_FOR"]     = new(RelationTypeRank.Oppositional, Symmetry.Asymmetric, null),
        ["NOT_CAPABLE_OF"]   = new(RelationTypeRank.Oppositional, Symmetry.Asymmetric, null),
        ["NOT_HAS_PROPERTY"] = new(RelationTypeRank.Oppositional, Symmetry.Asymmetric, null),

        ["RELATED_TO"]             = new(RelationTypeRank.Associative, Symmetry.Symmetric, null),
        ["PRECEDES"]               = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["DERIVATIONALLY_RELATED"] = new(RelationTypeRank.Associative, Symmetry.Symmetric, "RELATED_TO"),
        ["HAS_DEFINITION"]         = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_EXAMPLE"]            = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_ETYMOLOGY"]          = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["DEPICTS"]                = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["CAPTIONS"]               = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["ADJACENT_TO_PIXEL"]      = new(RelationTypeRank.Associative, Symmetry.Symmetric, null),
        ["PERTAINS_TO"]            = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "RELATED_TO"),
        ["ALSO_SEE"]               = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "RELATED_TO"),
        ["IN_VERB_GROUP_WITH"]     = new(RelationTypeRank.Associative, Symmetry.Symmetric, "RELATED_TO"),
        ["HAS_DOMAIN_TOPIC"]       = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["IS_COORDINATE_TERM_WITH"] = new(RelationTypeRank.Associative, Symmetry.Symmetric, "RELATED_TO"),
        ["HAS_USAGE_REGISTER"]      = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_VERB_FRAME"]          = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_DBPEDIA_RELATION"]    = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_DOMAIN_REGION"]      = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["HAS_DOMAIN_USAGE"]       = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["USED_FOR"]               = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["CAPABLE_OF"]             = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["AT_LOCATION"]            = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["LOCATED_NEAR"]           = new(RelationTypeRank.Associative, Symmetry.Symmetric, null),
        ["HAS_CONTEXT"]            = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["DESIRES"]                = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["RECEIVES_ACTION"]        = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["SYMBOL_OF"]              = new(RelationTypeRank.Associative, Symmetry.Asymmetric, null),
        ["DERIVED_FROM"]           = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "DERIVATIONALLY_RELATED"),
        ["ETYMOLOGICALLY_RELATED_TO"]   = new(RelationTypeRank.Associative, Symmetry.Symmetric, "HAS_ETYMOLOGY"),
        ["ETYMOLOGICALLY_DERIVED_FROM"] = new(RelationTypeRank.Associative, Symmetry.Asymmetric, "HAS_ETYMOLOGY"),

        ["EMBEDS"]          = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["Q_PROJECTS"]      = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["K_PROJECTS"]      = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["V_PROJECTS"]      = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["O_PROJECTS"]      = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["GATES"]           = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["UP_PROJECTS"]     = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["DOWN_PROJECTS"]   = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["NORM_SCALES"]     = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["OUTPUT_PROJECTS"] = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["TOKEN_MAPS_TO"]   = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),
        ["MERGES_WITH"]     = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),

        ["ATTENDS"]      = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, "RELATED_TO"),
        ["OV_RELATES"]   = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, "RELATED_TO"),
        ["COMPLETES_TO"] = new(RelationTypeRank.TensorCalculation, Symmetry.Asymmetric, null),

        ["HAS_EXTERNAL_ID"] = new(RelationTypeRank.ScalarValued, Symmetry.Asymmetric, null),
    };

    private static readonly Dictionary<string, (string Canon, bool Flip)> Alias = new(StringComparer.Ordinal)
    {
        ["HAS_UPOS"]         = ("HAS_POS", false),

        ["DEFINES"]    = ("HAS_DEFINITION", false),
        ["DEFINED_AS"] = ("HAS_DEFINITION", false),

        ["HAS_HYPERNYM"]   = ("IS_A", false),
        ["IS_HYPERNYM_OF"] = ("IS_A", true),
        ["HAS_HYPONYM"]    = ("IS_A", true),
        ["IS_HYPONYM_OF"]  = ("IS_A", false),
        ["HAS_INSTANCE"]   = ("IS_INSTANCE_OF", true),

        ["IS_PART_OF"]      = ("HAS_PART", true),
        ["IS_MEMBER_OF"]    = ("HAS_MEMBER", true),
        ["IS_SUBSTANCE_OF"] = ("HAS_SUBSTANCE", true),

        ["IS_DOMAIN_TOPIC_MEMBER"]  = ("HAS_DOMAIN_TOPIC", true),
        ["IS_DOMAIN_REGION_MEMBER"] = ("HAS_DOMAIN_REGION", true),
        ["IS_DOMAIN_USAGE_MEMBER"]  = ("HAS_DOMAIN_USAGE", true),

        ["HAS_SENSE_OF"] = ("IS_SENSE_OF", false),

        ["FOLLOWS"] = ("PRECEDES", true),

        ["SIMILAR_TO"] = ("IS_SIMILAR_TO", false),
        ["MADE_OF"]    = ("HAS_SUBSTANCE", false),

        ["INHERITS_FROM"] = ("IS_A", false),
        ["SUBFRAME_OF"]   = ("HAS_SUBEVENT", true),
        ["IS_INHERITED_BY"] = ("IS_A", true),

        ["HINDERED_BY"]  = ("OBSTRUCTED_BY", false),
        ["IS_FILLED_BY"] = ("X_FILLED_BY", false),
    };

    public static RelationTypeResolution Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        bool flip = false;
        if (Alias.TryGetValue(name, out var a)) { flip = a.Flip; name = a.Canon; }

        if (Canon.TryGetValue(name, out var def))
            return new RelationTypeResolution(
                RelationTypeId(name), def.Rank, def.Symmetry, flip,
                def.Parent is null ? null : RelationTypeId(def.Parent), name);

        return new RelationTypeResolution(RelationTypeId(name), RelationTypeRank.Probationary, Symmetry.Asymmetric, flip, null, name);
    }

    public static RelationTypeResolution ResolveDeprel(string deprel)
    {
        ArgumentException.ThrowIfNullOrEmpty(deprel);
        string norm = deprel.Trim().ToLowerInvariant();
        string canon = "DEP_" + norm.Replace(':', '_').ToUpperInvariant();
        int colon = norm.IndexOf(':');
        string parent = colon > 0 ? "DEP_" + norm[..colon].ToUpperInvariant() : "DEPENDS_ON";
        return new RelationTypeResolution(RelationTypeId(canon), RelationTypeRank.Partitive, Symmetry.Asymmetric, false, RelationTypeId(parent), canon);
    }

    public static AttestationRow AttestEnhancedDeprel(
        Hash128 dependent, string deprel, Hash128 head, Hash128 sourceId, double sourceTrust,
        long observationCount = 1)
    {
        var r = ResolveEnhancedDeprel(deprel);
        return AttestationFactory.CreateCategorical(
            dependent, r.Id, head, sourceId, null, confirm: true,
            witnessWeight: r.Rank * sourceTrust, observationCount: observationCount);
    }

    public static RelationTypeResolution ResolveEnhancedDeprel(string deprel)
    {
        ArgumentException.ThrowIfNullOrEmpty(deprel);
        string norm = deprel.Trim().ToLowerInvariant();
        string canon = "EDEP_" + norm.Replace(':', '_').ToUpperInvariant();
        int colon = norm.IndexOf(':');
        string parent = colon > 0 ? "EDEP_" + norm[..colon].ToUpperInvariant() : "ENHANCED_DEPENDS_ON";
        return new RelationTypeResolution(RelationTypeId(canon), RelationTypeRank.Partitive, Symmetry.Asymmetric, false, RelationTypeId(parent), canon);
    }

    public static RelationTypeResolution ResolveDbpedia(string rel)
    {
        ArgumentException.ThrowIfNullOrEmpty(rel);
        string norm = rel.Trim();
        if (norm.StartsWith("dbpedia/", StringComparison.OrdinalIgnoreCase)) norm = norm[8..];
        string canon = "DBPEDIA_" + norm.Replace('/', '_').ToUpperInvariant();
        return new RelationTypeResolution(RelationTypeId(canon), RelationTypeRank.Associative, Symmetry.Asymmetric, false,
                                  RelationTypeId("HAS_DBPEDIA_RELATION"), canon);
    }

    private static (Hash128 Subject, Hash128? Object) Orient(in RelationTypeResolution r, Hash128 subject, Hash128? obj)
    {
        if (obj is { } o)
        {
            if (r.Flip) (subject, o) = (o, subject);
            if (r.Symmetry == Symmetry.Symmetric && subject.CompareToBytewise(o) > 0)
                (subject, o) = (o, subject);
            return (subject, o);
        }
        return (subject, null);
    }

    public static AttestationRow Attest(
        Hash128 subject, string typeName, Hash128? obj, Hash128 sourceId, double sourceTrust,
        Hash128? contextId = null, bool confirm = true, long observationCount = 1)
    {
        var r = Resolve(typeName);
        var (s, o) = Orient(r, subject, obj);
        return AttestationFactory.CreateCategorical(
            s, r.Id, o, sourceId, contextId, confirm, r.Rank * sourceTrust, observationCount);
    }

    public static AttestationRow AttestWeighted(
        Hash128 subject, string typeName, Hash128? obj, Hash128 sourceId, double sourceTrust,
        double magnitude, double arenaScale, Hash128? contextId = null, long observationCount = 1)
    {
        var r = Resolve(typeName);
        var (s, o) = Orient(r, subject, obj);
        return AttestationFactory.CreateWeighted(
            s, r.Id, o, sourceId, contextId, r.Rank, sourceTrust, magnitude, arenaScale, observationCount);
    }

    public static AttestationRow AttestDeprel(
        Hash128 dependent, string deprel, Hash128 head, Hash128 sourceId, double sourceTrust,
        long observationCount = 1)
    {
        var r = ResolveDeprel(deprel);
        return AttestationFactory.CreateCategorical(
            dependent, r.Id, head, sourceId, null, confirm: true,
            witnessWeight: r.Rank * sourceTrust, observationCount: observationCount);
    }

    public static bool ParseFeature(string feature, out string name, out string value)
    {
        name = ""; value = "";
        if (string.IsNullOrEmpty(feature)) return false;
        int eq = feature.IndexOf('=');
        if (eq <= 0 || eq >= feature.Length - 1) return false;
        name = feature[..eq].Trim();
        value = feature[(eq + 1)..].Trim();
        return name.Length > 0 && value.Length > 0;
    }

    public static RelationTypeResolution ResolveFeature(string featureName)
    {
        ArgumentException.ThrowIfNullOrEmpty(featureName);
        string canon = "FEAT_" + featureName.Trim().ToUpperInvariant();
        return new RelationTypeResolution(RelationTypeId(canon), RelationTypeRank.Partitive, Symmetry.Asymmetric, false,
                                  RelationTypeId("HAS_FEATURE"), canon);
    }

    public static AttestationRow AttestFeature(
        Hash128 subject, string featureName, Hash128 valueEntity, Hash128 sourceId, double sourceTrust,
        long observationCount = 1)
    {
        var r = ResolveFeature(featureName);
        return AttestationFactory.CreateCategorical(
            subject, r.Id, valueEntity, sourceId, null, confirm: true,
            witnessWeight: r.Rank * sourceTrust, observationCount: observationCount);
    }

    public static IEnumerable<RelationTypeResolution> AllCanonical()
    {
        foreach (var name in Canon.Keys)
            yield return Resolve(name);
    }

    public static void SeedCanonical(SubstrateChangeBuilder builder, Hash128 sourceId)
    {
        var all = new List<RelationTypeResolution>(AllCanonical());
        foreach (var k in all)
            builder.AddEntity(new EntityRow(k.Id, (byte)MetaTier.RelationType, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
        foreach (var k in all)
            if (k.ParentId is { } parent)
                builder.AddAttestation(Attest(k.Id, "IS_A", parent, sourceId, SourceTrust.SubstrateMandate));
    }

    public static void SeedDynamic(SubstrateChangeBuilder builder, in RelationTypeResolution k, Hash128 sourceId,
                                   ISet<Hash128> seenEntitiesThisBatch,
                                   ConcurrentIdSet seenAttestationsThisRun)
    {
        if (seenEntitiesThisBatch.Add(k.Id))
            builder.AddEntity(new EntityRow(k.Id, (byte)MetaTier.RelationType, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
        if (k.ParentId is { } parent && seenAttestationsThisRun.Add(k.Id))
        {
            builder.AddEntity(new EntityRow(k.Id, (byte)MetaTier.RelationType, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
            builder.AddEntity(new EntityRow(parent, (byte)MetaTier.RelationType, BootstrapIntentBuilder.RelationTypeMetaTypeId, sourceId));
            builder.AddAttestation(Attest(k.Id, "IS_A", parent, sourceId, SourceTrust.AcademicCurated));
        }
    }

    public static void SeedDeprel(SubstrateChangeBuilder builder, string deprel, Hash128 sourceId,
                                  ISet<Hash128> seenEntitiesThisBatch,
                                  ConcurrentIdSet seenAttestationsThisRun)
    {
        int colon = deprel.IndexOf(':');
        if (colon > 0) SeedDynamic(builder, ResolveDeprel(deprel[..colon]), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun);
        SeedDynamic(builder, ResolveDeprel(deprel), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun);
    }

    public static void SeedEnhancedDeprel(SubstrateChangeBuilder builder, string deprel, Hash128 sourceId,
                                          ISet<Hash128> seenEntitiesThisBatch,
                                          ConcurrentIdSet seenAttestationsThisRun)
    {
        int colon = deprel.IndexOf(':');
        if (colon > 0) SeedDynamic(builder, ResolveEnhancedDeprel(deprel[..colon]), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun);
        SeedDynamic(builder, ResolveEnhancedDeprel(deprel), sourceId, seenEntitiesThisBatch, seenAttestationsThisRun);
    }
}
