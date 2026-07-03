namespace Laplace.Engine.Core;

public static class IngestBaselineGates
{
    public const long MinWriterRowsPerSecond = 500_000;

    public const double MaxSecondsPerGigabyte = 30.0;

    public static double MinMegabytesPerSecond =>
        1024.0 / MaxSecondsPerGigabyte;

    public static double MinBytesPerSecond =>
        (1024.0 * 1024.0 * 1024.0) / MaxSecondsPerGigabyte;

    public static double MaxSecondsForBytes(long inputBytes) =>
        MaxSecondsPerGigabyte * (inputBytes / (1024.0 * 1024.0 * 1024.0));

    public const int MaxRoundTripsPerApplyBatch = 12;
}
