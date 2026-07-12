using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.UD;

public readonly struct UDSource : ISeedSource
{
    public static Hash128 SourceId { get; } =
        Hash128.OfCanonical("substrate/source/UDDecomposer/v1");

    public static string SourceName => "UDDecomposer";

    public static Hash128 TrustClass { get; } =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public static IReadOnlyList<string> Relations { get; } =
        ["HAS_DEFINITION", "TRANSCRIBES_AS", "ENHANCED_DEPENDS_ON",
         "HAS_POS", "HAS_XPOS", "HAS_LANGUAGE", "IS_A"];

    public static IReadOnlyList<string>? TypeNodeNames { get; } =
        ["UD_Feature"];

    public static SourceLicense License => SourceLicense.Unknown;

    public static IngestSourceProfile Profile => IngestSourceProfile.UdSentence;
}
