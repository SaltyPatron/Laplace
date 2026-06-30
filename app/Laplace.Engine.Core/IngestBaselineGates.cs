namespace Laplace.Engine.Core;

/// <summary>
/// Committed ingest throughput gates (hardware of record: i9-14900KS, PG18 native Windows,
/// RAID-0 NVMe). Every gate has a matching test in SubstrateCRUD.Tests or Ingestion.Tests.
/// </summary>
public static class IngestBaselineGates
{
    /// <summary>Novel-row apply_batch throughput (native COPY + one SPI merge per commit).</summary>
    public const long MinWriterRowsPerSecond = 500_000;

    /// <summary>Warm re-ingest: input bytes scanned per wall second (descent + session cache hot).</summary>
    public const double MaxSecondsPerGigabyte = 30.0;

    /// <summary>Input mebibytes per second floor derived from <see cref="MaxSecondsPerGigabyte"/>.</summary>
    public static double MinMegabytesPerSecond =>
        1024.0 / MaxSecondsPerGigabyte;

    public static double MinBytesPerSecond =>
        (1024.0 * 1024.0 * 1024.0) / MaxSecondsPerGigabyte;

    public static double MaxSecondsForBytes(long inputBytes) =>
        MaxSecondsPerGigabyte * (inputBytes / (1024.0 * 1024.0 * 1024.0));

    /// <summary>Single-commit apply round-trips with <c>LAPLACE_APPLY_PARTITIONS=1</c>.</summary>
    public const int MaxRoundTripsPerApplyBatch = 12;
}
