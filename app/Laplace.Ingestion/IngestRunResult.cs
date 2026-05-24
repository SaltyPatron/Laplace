using Laplace.Engine.Core;

namespace Laplace.Ingestion;

/// <summary>Aggregate per-run statistics returned by
/// <see cref="IngestRunner.RunAsync"/>.</summary>
public sealed record IngestRunResult(
    Hash128                   SourceId,
    string                    SourceName,
    long                      UnitsAttempted,
    long                      UnitsApplied,
    long                      UnitsSkippedFromCheckpoint,
    long                      UnitsFailed,
    long                      EntitiesInserted,
    long                      PhysicalitiesInserted,
    long                      AttestationsInserted,
    long                      TotalRoundTrips,
    TimeSpan                  WallClock,
    IReadOnlyList<IngestFailure> Failures);
