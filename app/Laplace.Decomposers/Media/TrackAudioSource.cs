using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Media;

/// <summary>Generic audio-track lane (fixture / corpus-agnostic). Not a corpus source.</summary>
public readonly struct TrackAudioSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("TrackAudioDecomposer");

    public static string SourceName => "TrackAudioDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_SPECTRAL_PEAK", "HAS_ONSET_SEGMENT"];

    public static IReadOnlyList<string>? TypeNodeNames =>
        ["Sample", "Frame", "OnsetSegment", "Phrase", "Track"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.MediaAudio;
}
