namespace Laplace.Decomposers.Abstractions;

public static class BatchConfigDefaults
{
    public const int Text = 4096;
    public const int Code = 512;
    public const int Chess = 512;
    public const int ChessOpening = 1024;
    public const int HighVolume = 65536;
    public const int Structural = 2048;
    public const int Ud = 512;
    public const int Document = 32;

    public static int Resolve(DecomposerOptions? options, int defaultSize) =>
        options is { BatchSize: > 1 } ? options.BatchSize : defaultSize;
}
