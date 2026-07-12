namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// SPDX / attribution metadata for a seed source. Deposited as witnessed credit
/// attestations in Wave 5; Wave 2+ carries it on <see cref="ISeedSource"/> /
/// <see cref="ISourceManifest"/> so Initialize can register the hook point.
/// </summary>
public sealed record SourceLicense(
    string Name,
    string? Spdx = null,
    string? Url = null,
    string? Copyright = null,
    string? Citation = null,
    string? Version = null)
{
    public static SourceLicense Unknown { get; } = new("Unknown");
}
