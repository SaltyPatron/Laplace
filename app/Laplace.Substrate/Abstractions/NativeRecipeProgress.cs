namespace Laplace.Decomposers.Abstractions;

/// <summary>Operational counters emitted by the Native producer, not parsed console prose.</summary>
public sealed record NativeRecipeProgress(
    string Event, string? Source, int FilesTotal, int FilesDone, long BytesDone,
    long Entities, long Physicalities, string? File, long FileBytes,
    long Records, string? FileId, string? SourceId);
