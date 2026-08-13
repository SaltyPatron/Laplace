using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.UD;

public readonly struct UDSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        SubstrateCanonicalIds.Source("UDDecomposer");

    public static string SourceName => "UDDecomposer";

    public static Hash128 TrustClass { get; } =
        SubstrateCanonicalIds.TrustClass("AcademicCurated");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_DEFINITION", "TRANSCRIBES_AS", "ENHANCED_DEPENDS_ON",
         "HAS_POS", "HAS_XPOS", "HAS_LANGUAGE", "IS_A",
         // Emitted by UdSentenceEmitter all along but never declared here.
         "IS_LEMMA_OF", "HAS_PART",
         // The BASIC dependency family — 160k live rows before this line existed,
         // while the list carried only the enhanced family (#1057 defect 6).
         "DEPENDS_ON"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["UD_Feature"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.UdSentence;
}
