using Laplace.Engine.Core;

namespace Laplace.Ingestion;

public sealed record IngestRunResult(
    Hash128 SourceId,
    string SourceName,
    long UnitsAttempted,
    long UnitsApplied,
    long UnitsFailed,
    long EntitiesInserted,
    long PhysicalitiesInserted,
    long AttestationsInserted,
    long TotalRoundTrips,
    TimeSpan WallClock,
    IReadOnlyList<IngestFailure> Failures,
    // The run's OWN final file count, so the terminal journal write and DeriveRunStatus
    // read the same number. Without it the row kept whatever the last periodic progress
    // flush left: OMW derived ok from 1226 == 1226 in memory while the ledger held
    // files_done 1225 of 1226, and the row is the only surviving artifact of a run.
    int FilesDone = 0,
    // Managed structural identities deliberately admitted without content geometry.
    // Kept separate from the entity/physicality delta: POS, ordinals and source keys
    // are not off-DAG content merely because they are substrate entities.
    int GovernedIdentitiesWithoutPhysicality = 0);
