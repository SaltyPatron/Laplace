using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

internal enum DocumentRelation
{
    Contains,
    Expresses,
    HasTitle,
    AuthoredBy,
}

/// <summary>
/// Corpus/runtime document ingestion is not conversational testimony.  The decomposer
/// owns this run-level source identity; individual content rows carry the nearest
/// structural trunk (document -> file -> this source) as provenance.
/// </summary>
public readonly struct DocumentSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("DocumentDecomposer");

    public static string SourceName => "DocumentDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["CONTAINS", "EXPRESSES", "HAS_TITLE", "AUTHORED_BY"];

    private static readonly RelationTypeRegistry.RelationTypeResolution[] Resolutions =
        Relations.Select(RelationTypeRegistry.Resolve).ToArray();

    internal static RelationTypeRegistry.RelationTypeResolution Resolve(DocumentRelation relation) =>
        Resolutions[(int)relation];

    public static IReadOnlyList<string>? TypeNodeNames { get; } = ["Document", "SourceFile"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Document;
}
