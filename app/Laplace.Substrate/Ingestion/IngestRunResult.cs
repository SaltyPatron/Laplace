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
    // The run's final file count, so the terminal journal write and DeriveRunStatus read
    // the same number rather than the last periodic progress flush.
    int FilesDone = 0,
    // Exact terminal extraction counters. Periodic observability is throttled, so these,
    // not the last heartbeat, are what the runner hands to LapSight.
    long InputUnitsDone = 0,
    long InputUnitsTotal = 0,
    // Initialization rows are included in the totals above, but split out so LapSight
    // can distinguish fixed vocabulary/bootstrap overhead from input amplification.
    long BootstrapEntitiesInserted = 0,
    long BootstrapPhysicalitiesInserted = 0,
    long BootstrapAttestationsInserted = 0,
    long ConsensusObservations = 0,
    long ConsensusCellDeposits = 0)
{
    public long BootstrapRowsInserted =>
        BootstrapEntitiesInserted + BootstrapPhysicalitiesInserted + BootstrapAttestationsInserted;

    public long PayloadEntitiesInserted => EntitiesInserted - BootstrapEntitiesInserted;
    public long PayloadPhysicalitiesInserted => PhysicalitiesInserted - BootstrapPhysicalitiesInserted;
    public long PayloadAttestationsInserted => AttestationsInserted - BootstrapAttestationsInserted;
    public long PayloadRowsInserted =>
        PayloadEntitiesInserted + PayloadPhysicalitiesInserted + PayloadAttestationsInserted;

    public double PayloadRowsPerInput =>
        InputUnitsDone > 0 ? (double)PayloadRowsInserted / InputUnitsDone : 0;

    public double ObservationsPerCellDeposit =>
        ConsensusCellDeposits > 0 ? (double)ConsensusObservations / ConsensusCellDeposits : 0;
}
