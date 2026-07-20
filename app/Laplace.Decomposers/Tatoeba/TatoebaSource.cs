using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Tatoeba;

public readonly struct TatoebaSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("TatoebaDecomposer");

    public static string SourceName => "TatoebaDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_EXTERNAL_ID", "IS_TRANSLATION_OF", "HAS_LANGUAGE"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["Tatoeba_Sentence"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
