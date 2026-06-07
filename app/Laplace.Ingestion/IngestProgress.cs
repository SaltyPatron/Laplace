namespace Laplace.Ingestion;

public sealed record IngestProgress(
    long  UnitsAttempted,
    long  UnitsApplied,
    long  UnitsFailed,
    long? EstimatedTotal,
    TimeSpan Elapsed,
    long  EntitiesInserted = 0,
    long  PhysicalitiesInserted = 0,
    long  AttestationsInserted = 0);
