namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Identity-bearing facts parsed from a file's own format metadata. Values remain
/// source-native text (with only line-fold whitespace collapsed); no filename or
/// ambient catalog value may fill an absent field.
/// </summary>
public sealed record DocumentFormatMetadata(
    string Format,
    string? EbookId = null,
    string? Title = null,
    string? Author = null,
    string? Language = null,
    string? ReleaseDate = null,
    string? UpdatedDate = null,
    string? Credits = null,
    long? HeaderBoundaryByteOffset = null,
    string? HeaderBoundary = null,
    string HeaderStatus = "complete");
