using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.OpenSubtitles;

public readonly struct OpenSubtitlesSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("OpenSubtitlesDecomposer");

    public static string SourceName => "OpenSubtitlesDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        [EtlSource.LanguageScopeRelation];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["OpenSubtitles_Sequence", "OpenSubtitles_Alignment"];

    public static SourceLicense License => SourceLicense.Unknown;

    private static readonly IngestSourceProfile AlignedBlockProfile =
        new(1_048_576, OpenSubtitlesZipIngest.BlockPairs * 2,
            ResidentBytesPerComposeUnit: 8_192);

    public static IngestSourceProfile Profile => AlignedBlockProfile;
}
