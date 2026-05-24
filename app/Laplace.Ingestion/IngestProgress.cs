namespace Laplace.Ingestion;

/// <summary>Periodic progress signal emitted via
/// <see cref="IngestRunOptions.Progress"/>.</summary>
public sealed record IngestProgress(
    long  UnitsAttempted,
    long  UnitsApplied,
    long  UnitsSkippedFromCheckpoint,
    long  UnitsFailed,
    long? EstimatedTotal,        // null if EstimateUnitCountAsync returned null
    TimeSpan Elapsed);
