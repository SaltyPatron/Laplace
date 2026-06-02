namespace Laplace.Ingestion;

/// <summary>Periodic progress signal emitted via
/// <see cref="IngestRunOptions.Progress"/>.</summary>
public sealed record IngestProgress(
    long  UnitsAttempted,
    long  UnitsApplied,
    long  UnitsFailed,
    long? EstimatedTotal,        // null if EstimateUnitCountAsync returned null
    TimeSpan Elapsed,
    // What was actually RECORDED (not opaque "intents") — so a log line can show
    // entities/physicalities/attestations landed + rows/s, the human-valued metric.
    long  EntitiesInserted = 0,
    long  PhysicalitiesInserted = 0,
    long  AttestationsInserted = 0);
