using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.UD;

public readonly struct UDSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("UDDecomposer");

    public static string SourceName => "UDDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_LANGUAGE", "IS_A", "HAS_PARSE"];

    internal static readonly Hash128 HasParseTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[2]);
    internal static readonly Hash128 IsATypeId =
        RelationTypeRegistry.RelationTypeId(Relations[1]);
    internal static readonly Hash128 HasLanguageTypeId =
        RelationTypeRegistry.RelationTypeId(Relations[0]);

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["UD_Annotation_Marker", "UD_Annotation_Value", "UD_Feature", "UD_Parse",
         "UD_Parse_Occurrence", "UD_Token_Ref", "UD_XPOS"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.UdSentence;
}
