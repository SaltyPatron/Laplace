using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Media;

/// <summary>Source identity for audio tracks, independent of any corpus.</summary>
public readonly struct TrackAudioSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("TrackAudioDecomposer");

    public static string SourceName => "TrackAudioDecomposer";

    public static Hash128 TrustClass { get; } =
        TrustClassRegistry.Id("StructuredCorpus");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_SPECTRAL_PEAK", "HAS_ONSET_SEGMENT"];

    // Tier names match the native audio ladder: tier 2 is "Window", whose type id is
    // blake3("Window") (laplace_modality_tier_type_id).
    public static IReadOnlyList<string>? TypeNodeNames =>
        ["Sample", "Window", "OnsetSegment", "Phrase", "Track"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.MediaAudio;
}
