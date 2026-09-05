using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.OMW;

internal enum OmwRelation
{
    HasDefinition, HasExample, IsSynonymOf, HasLanguage, HasPos, IsTypedAs,
    HasNameAlias, HasVersion, HasLicense, HasSourceUrl, HasCitation, HasAttribution,
    Contains, Requires, FormOf, HasSense, IsSenseOf, HasSenseFrequency, HasFeature,
    CorrespondsTo, HasVerbFrame, HasLexCategory, HasMember, HasProperty, IsAntonymOf,
    HasHypernym, IsInstanceOf, HasHyponym, HasInstance, IsMemberOf, IsSubstanceOf,
    IsPartOf, HasSubstance, HasPart, HasAttribute, DerivationallyRelated,
    HasDomainTopic, IsDomainTopicMember, HasDomainRegion, IsDomainRegionMember,
    Entails, Causes, AlsoSee, IsSimilarTo, IsParticipleOf, PertainsTo,
    HasDomainUsage, IsDomainUsageMember,
}

public readonly struct OMWSource : ISeedSource
{
    public static Hash128 LexiconTypeId { get; } = EntityTypeRegistry.Id("OMW_Lexicon");
    public static Hash128 LexicalEntryTypeId { get; } = EntityTypeRegistry.Id("OMW_Lexical_Entry");
    public static Hash128 SyntacticBehaviourTypeId { get; } =
        EntityTypeRegistry.Id("OMW_Syntactic_Behaviour");
    public static Hash128 PackageTypeId { get; } = EntityTypeRegistry.Id("OMW_Package");

    public static Hash128 SourceId { get; } = SubstrateCanonicalIds.Source("OMWDecomposer");
    public static string SourceName => "OMWDecomposer";
    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    // Single declaration and lookup roster: call sites resolve indexed ids from here.
    public static IReadOnlyList<string> Relations { get; } =
    [
        "HAS_DEFINITION", "HAS_EXAMPLE", "IS_SYNONYM_OF", "HAS_LANGUAGE", "HAS_POS",
        "IS_TYPED_AS", "HAS_NAME_ALIAS", "HAS_VERSION", "HAS_LICENSE", "HAS_SOURCE_URL",
        "HAS_CITATION", "HAS_ATTRIBUTION", "CONTAINS", "REQUIRES", "FORM_OF", "HAS_SENSE",
        "IS_SENSE_OF", "HAS_SENSE_FREQUENCY", "HAS_FEATURE", "CORRESPONDS_TO",
        "HAS_VERB_FRAME", "HAS_LEX_CATEGORY", "HAS_MEMBER", "HAS_PROPERTY", "IS_ANTONYM_OF",
        "HAS_HYPERNYM", "IS_INSTANCE_OF", "HAS_HYPONYM", "HAS_INSTANCE", "IS_MEMBER_OF",
        "IS_SUBSTANCE_OF", "IS_PART_OF", "HAS_SUBSTANCE", "HAS_PART", "HAS_ATTRIBUTE",
        "DERIVATIONALLY_RELATED", "HAS_DOMAIN_TOPIC", "IS_DOMAIN_TOPIC_MEMBER",
        "HAS_DOMAIN_REGION", "IS_DOMAIN_REGION_MEMBER", "ENTAILS", "CAUSES", "ALSO_SEE",
        "IS_SIMILAR_TO", "IS_PARTICIPLE_OF", "PERTAINS_TO",
        "HAS_DOMAIN_USAGE", "IS_DOMAIN_USAGE_MEMBER",
    ];

    private static readonly RelationTypeRegistry.RelationTypeResolution[] Resolutions =
        Relations.Select(RelationTypeRegistry.Resolve).ToArray();

    internal static RelationTypeRegistry.RelationTypeResolution Resolve(OmwRelation relation) =>
        Resolutions[(int)relation];

    public static IReadOnlyList<string>? TypeNodeNames =>
        ["OMW_Lexicon", "OMW_Lexical_Entry", "OMW_Syntactic_Behaviour", "OMW_Package",
         "WordNet_Sense", "WordNet_Synset", "Source_Reference"];

    public static SourceLicense License => SourceLicense.Unknown;
    public static IngestSourceProfile Profile => IngestSourceProfile.Omw;
}
