using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

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

    public static IReadOnlyList<string> Relations { get; } = ["CONTAINS"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } = ["Document", "SourceFile"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Document;
}
