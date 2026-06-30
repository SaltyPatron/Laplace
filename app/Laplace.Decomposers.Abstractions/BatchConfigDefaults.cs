namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Default batch sizes per modality. Each default is tuned to the modality's median row size and
/// PG batch-insert throughput characteristics. Callers override via <see cref="DecomposerOptions.BatchSize"/>.
/// </summary>
public static class BatchConfigDefaults
{
    public const int Text        = 4096;   // FrameNet, PropBank, VerbNet, Atomic2020, Tabular
    public const int Code        = 512;    // code repos, stack traces, tiny-codes
    public const int Chess       = 512;    // PGN moves; openings use 1024 (larger but fewer)
    public const int ChessOpening = 1024;
    public const int HighVolume  = 65536;  // OpenSubtitles, Tatoeba, ConceptNet
    public const int Structural  = 2048;   // ETL generic, OMW
    public const int Ud          = 512;    // UD sentences (capped by UdIngestAdapter.MaxBatch)
    public const int Document    = 32;     // large structured documents

    /// <summary>Return <paramref name="options"/> batch size if set, otherwise <paramref name="defaultSize"/>.</summary>
    public static int Resolve(DecomposerOptions? options, int defaultSize) =>
        options is { BatchSize: > 1 } ? options.BatchSize : defaultSize;
}
