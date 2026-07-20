using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Code;

public readonly struct TabularSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("TabularDecomposer");

    public static string SourceName => "TabularDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["PREDICTS", "IS_VALUE_IN", "IS_INSTANCE_OF"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["TabularColumn", "TabularValue", "TabularOutcome"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
