using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Media;

public readonly struct RgbaImageSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("RgbaImageDecomposer");

    public static string SourceName => "RgbaImageDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_REGION", "HAS_PATCH", "IS_PIXEL_OF", "ADJACENT_TO_PIXEL", "DEPICTS", "CAPTIONS"];

    public static IReadOnlyList<string>? TypeNodeNames =>
        ["Pixel", "Patch", "Region", "Image"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.MediaImage;
}
