using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Trait("Tier", "db")]
[Collection("substrate-pg")]
public sealed class IngestThroughputObservabilityTests
{
    private readonly LocalPgFixture _pg;

    public IngestThroughputObservabilityTests(LocalPgFixture pg) => _pg = pg;

    [Fact]
    public async Task RunFinished_PersistsRunnerWallClock_IntoSharedThroughputVerdict()
    {
        string sourceName = $"ThroughputManagedClockTest-{Guid.NewGuid():N}";
        var sourceId = SubstrateCanonicalIds.OfVersioned("source", "test", sourceName);
        var obs = new NpgsqlIngestObservability(_pg.DataSource);

        obs.OnRunStart(sourceName, layerOrder: 1, inventory: null);
        obs.OnRunFinished(
            sourceName,
            new IngestRunResult(
                SourceId: sourceId,
                SourceName: sourceName,
                UnitsAttempted: 1,
                UnitsApplied: 1,
                UnitsFailed: 0,
                EntitiesInserted: 1000,
                PhysicalitiesInserted: 0,
                AttestationsInserted: 0,
                TotalRoundTrips: 1,
                WallClock: TimeSpan.FromSeconds(10),
                Failures: Array.Empty<IngestFailure>(),
                InputUnitsDone: 1,
                InputUnitsTotal: 1),
            status: "ok");

        await using var cmd = _pg.DataSource.CreateCommand(@"
SELECT throughput_elapsed_ms,
       throughput_rows,
       throughput_rows_per_s,
       throughput_status,
       throughput_compared
FROM laplace.ingest_run_journal
WHERE source_name = $1
ORDER BY started_at DESC
LIMIT 1;");
        cmd.Parameters.AddWithValue(sourceName);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(10_000L, reader.GetInt64(0));
        Assert.Equal(1_000L, reader.GetInt64(1));
        Assert.InRange(reader.GetDouble(2), 99.999, 100.001);
        Assert.Equal("unbaselined", reader.GetString(3));
        Assert.False(reader.GetBoolean(4));

        var statusRows = await NpgsqlSubstrateReads.SourceStatusAsync(
            _pg.DataSource, sourceName, CancellationToken.None);
        var sourceStatus = Assert.Single(statusRows);
        Assert.Equal("unbaselined", sourceStatus.ThroughputStatus);
        Assert.False(sourceStatus.ThroughputCompared);
        Assert.InRange(sourceStatus.ThroughputRowsPerS!.Value, 99.999, 100.001);
        Assert.Null(sourceStatus.ThroughputBaselineRowsPerS);
        Assert.Null(sourceStatus.ThroughputSlowdownRatio);
    }
}
