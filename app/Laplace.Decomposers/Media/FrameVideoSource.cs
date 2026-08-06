using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Media;

public readonly struct FrameVideoSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("FrameVideoDecomposer");

    public static string SourceName => "FrameVideoDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_FRAME", "PRECEDES_IN_TIME", "HAS_REGION", "HAS_PATCH"];

    public static IReadOnlyList<string>? TypeNodeNames =>
        ["Pixel", "Patch", "Region", "Image", "Frame"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.MediaVideo;
}
