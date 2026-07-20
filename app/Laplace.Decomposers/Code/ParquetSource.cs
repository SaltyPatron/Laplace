using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Code;

/// <summary>
/// Seed source for the generic <see cref="ParquetDecomposer"/>. Mirrors
/// <see cref="TabularSource"/> — a structured-corpus witness under the same trust
/// class. Parquet is a container; the columns/rows are witnessed with the same
/// column/value grammar the CSV tabular lane uses, so both converge on the shared
/// <c>TabularColumn</c>/<c>TabularValue</c> entity types.
/// </summary>
public readonly struct ParquetSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("ParquetDecomposer");

    public static string SourceName => "ParquetDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["IS_VALUE_IN", "IS_INSTANCE_OF"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["TabularColumn", "TabularValue"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.Default;
}
