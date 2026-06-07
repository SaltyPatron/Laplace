using Laplace.Engine.Core;

namespace Laplace.Ingestion;

public sealed record IngestRunResult(
    Hash128                   SourceId,
    string                    SourceName,
    long                      UnitsAttempted,
    long                      UnitsApplied,
    long                      UnitsFailed,
    long                      EntitiesInserted,
    long                      PhysicalitiesInserted,
    long                      AttestationsInserted,
    long                      TotalRoundTrips,
    TimeSpan                  WallClock,
    IReadOnlyList<IngestFailure> Failures);
