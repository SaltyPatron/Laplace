using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Code;

/// <summary>
/// Source identity for <see cref="ParquetDecomposer"/>, under the same trust class as
/// <see cref="TabularSource"/>. Parquet columns/rows use the same column/value grammar as
/// CSV tables, so both converge on the shared <c>TabularColumn</c>/<c>TabularValue</c> types.
/// </summary>
public readonly struct ParquetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("ParquetDecomposer");

    public static string SourceName => "ParquetDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["IS_VALUE_IN", "IS_INSTANCE_OF"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["TabularColumn", "TabularValue"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
