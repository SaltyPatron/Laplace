using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Tatoeba;

public readonly struct TatoebaSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/TatoebaDecomposer/v1");

    public static string SourceName => "TatoebaDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/StructuredCorpus/v1");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_EXTERNAL_ID", "IS_TRANSLATION_OF", "HAS_LANGUAGE"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["Tatoeba_Sentence"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
