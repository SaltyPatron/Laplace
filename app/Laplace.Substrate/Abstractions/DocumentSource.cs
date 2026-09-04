using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Seed/corpus document witness. This is deliberately NOT UserPrompt: a file ingested from
/// a corpus is not a conversational observation, and source-level queries must be able to
/// distinguish the two without inspecting filenames or workflow history.
///
/// Runtime user uploads should use a tenant/user-scoped witness source at the product edge;
/// the content/file identities remain shared and content-addressed, while provenance is not.
/// </summary>
public readonly struct DocumentSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("DocumentDecomposer");

    public static string SourceName => "DocumentDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } = [];

    public static IReadOnlyList<string>? TypeNodeNames => null;

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Document;

    public static double WitnessWeight =>
        RelationTypeRank.Associative * SourceTrust.StructuredCorpus;
}
