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

    // Tier 2 is "Window" — the native audio ladder hashes blake3("Window")
    // (laplace_modality_tier_type_id) and its tests pin it. "Frame" here was a
    // C#/native identity split: two different type ids for one tier.
    public static IReadOnlyList<string>? TypeNodeNames =>
        ["Sample", "Window", "OnsetSegment", "Phrase", "Track"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.MediaAudio;
}
