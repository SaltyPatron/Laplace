namespace Laplace.Ingestion;

/// <summary>ETL progress snapshot — input completion %, not output row inflation.</summary>
public sealed record IngestProgress(
    string   SourceName,
    int      LayerOrder,
    long     UnitsAttempted,
    long     UnitsApplied,
    long     UnitsFailed,
    long     InputUnitsTotal,
    long     InputUnitsDone,
    int      FilesTotal,
    int      FilesDone,
    string?  CurrentFile,
    string   UnitKind,
    TimeSpan Elapsed,
    long     EntitiesInserted = 0,
    long     PhysicalitiesInserted = 0,
    long     AttestationsInserted = 0,
    long     RoundTrips = 0,
    long     UnitsProduced = 0)
{
    public double InputPercent =>
        InputUnitsTotal > 0 ? 100.0 * InputUnitsDone / InputUnitsTotal
        : UnitsProduced > 0 ? 100.0 * UnitsApplied / UnitsProduced
        : 0;

    public double FilePercent =>
        FilesTotal > 0 ? 100.0 * FilesDone / FilesTotal : 0;
}
