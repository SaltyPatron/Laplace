using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.OpenSubtitles;

public readonly struct OpenSubtitlesSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/OpenSubtitlesDecomposer/v1");

    public static string SourceName => "OpenSubtitlesDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    public static IReadOnlyList<string> Relations { get; } =
        ["IS_TRANSLATION_OF", "HAS_LANGUAGE"];

    public static IReadOnlyList<string>? TypeNodeNames => null;

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.RelationTriple;
}
