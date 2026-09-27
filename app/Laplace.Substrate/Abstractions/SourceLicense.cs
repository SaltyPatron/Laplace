namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// SPDX / attribution metadata for a source, carried on <see cref="ISeedSource"/> /
/// <see cref="ISourceManifest"/>; <c>SourceVocabularyBootstrap</c> attests it on the source entity.
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
