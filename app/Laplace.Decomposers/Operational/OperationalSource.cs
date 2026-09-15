using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Operational;

/// <summary>Repository contract artifacts selected as Laplace's operational authority.</summary>
public readonly struct OperationalSource : ISeedSource
{
    public static Hash128 SourceId { get; } = SubstrateCanonicalIds.Source("OperationalDecomposer");
    public static string SourceName => "OperationalDecomposer";
    public static Hash128 TrustClass { get; } = SubstrateCanonicalIds.TrustClass("SubstrateMandate");
    public static IReadOnlyList<string> Relations { get; } =
        ["CONTAINS", "DEFINES", "CALLS", "REFERENCES"];
    public static IReadOnlyList<string>? TypeNodeNames { get; } = ["Document", "SourceFile"];
    public static IngestSourceProfile Profile => IngestSourceProfile.Document;
    public static SourceLicense License { get; } = new(
        "Proprietary — all rights reserved",
        Url: "https://github.com/SaltyPatron/Laplace/tree/main/docs",
        Citation: "Selected Laplace repository invention and binding specification artifacts, admitted unchanged",
        Version: "1");
}
