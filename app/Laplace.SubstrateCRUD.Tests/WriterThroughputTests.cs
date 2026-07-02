using System.Collections.Immutable;
using System.Diagnostics;
using global::Npgsql;
using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.SubstrateCRUD.Tests;

[Trait("Tier", "perf")]
[Collection("substrate-pg-writer-throughput")]
public sealed class EntityWriterThroughputTests
{
    private readonly LocalPgFixture _pg;

    private static readonly Hash128 ThroughputSrc =
        Hash128.OfCanonical("substrate/source/test/throughput-ent/v1");
    private static readonly Hash128 ThroughputTypeId =
        Hash128.OfCanonical("ThroughputFixture");

    public EntityWriterThroughputTests(LocalPgFixture pg) => _pg = pg;

    private Hash128 Id(int seed) => Hash128.Blake3(BitConverter.GetBytes(seed));

    [Fact]
    public async Task NativeStage_Exceeds_500k_RowsPerSecond()
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) VALUES "
          + "($1, 0::smallint, $1, NULL) ON CONFLICT (id) DO NOTHING");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, ThroughputTypeId.ToBytes());
        await cmd.ExecuteNonQueryAsync();

        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        const int totalRows = 500_000;
        var stage = IntentStage.New(totalRows);
        for (int i = 0; i < totalRows; i++)
            stage.AddEntity(Id(10_000_000 + i), 0, ThroughputTypeId, null);

        var change = WriterThroughputTests.NativeOnly(stage, ThroughputSrc, "tp-ent-native");
        var sw = Stopwatch.StartNew();
        var result = await writer.ApplyAsync(change);
        sw.Stop();

        Assert.Equal(totalRows, result.EntitiesInserted);
        double rowsPerSec = result.EntitiesInserted / sw.Elapsed.TotalSeconds;
        Assert.True(rowsPerSec >= IngestBaselineGates.MinWriterRowsPerSecond,
            $"Entity apply {rowsPerSec:F0} rows/sec is below the {IngestBaselineGates.MinWriterRowsPerSecond:N0} gate "
          + $"({result.EntitiesInserted:N0} inserted in {sw.Elapsed.TotalSeconds:F2}s, round_trips={result.RoundTrips})");
    }
}

[Trait("Tier", "perf")]
[Collection("substrate-pg-writer-throughput")]
public sealed class WriterThroughputTests
{
    private readonly LocalPgFixture _pg;

    private static readonly Hash128 ThroughputSrc =
        Hash128.OfCanonical("substrate/source/test/throughput/v1");
    private static readonly Hash128 ThroughputTypeId =
        Hash128.OfCanonical("ThroughputFixture");
    private static readonly Hash128 RelTypeId =
        Hash128.OfCanonical("ThroughputRelation");

    public WriterThroughputTests(LocalPgFixture pg) => _pg = pg;

    private static NpgsqlSubstrateWriter Writer(NpgsqlDataSource ds) =>
        new(ds);

    private Hash128 Id(int seed) => Hash128.Blake3(BitConverter.GetBytes(seed));

    private async Task EnsureVocabAsync()
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) VALUES "
          + "($1, 0::smallint, $1, NULL), ($2, 0::smallint, $1, NULL), ($3, 0::smallint, $1, NULL) "
          + "ON CONFLICT (id) DO NOTHING");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, ThroughputTypeId.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, ThroughputSrc.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, RelTypeId.ToBytes());
        await cmd.ExecuteNonQueryAsync();
    }

    internal static SubstrateChange NativeOnly(
        IntentStage stage, Hash128 src, string unitName, long inputUnits = 0)
    {
        return new SubstrateChange(
            ImmutableArray<EntityRow>.Empty,
            ImmutableArray<PhysicalityRow>.Empty,
            ImmutableArray<AttestationRow>.Empty,
            new SubstrateChangeMetadata(
                Hash128.Blake3(System.Text.Encoding.UTF8.GetBytes(unitName)),
                src,
                unitName,
                DateTimeOffset.UtcNow,
                null,
                InputUnitsConsumed: inputUnits),
            IntentStages: [stage]);
    }

    [Fact]
    public async Task Attestation_NativeStage_Exceeds_500k_RowsPerSecond()
    {
        await EnsureVocabAsync();
        var writer = Writer(_pg.DataSource);

        const int totalRows = 500_000;
        int seedBase = 20_000_000;

        var seedStage = IntentStage.New(totalRows * 2);
        for (int i = 0; i < totalRows * 2; i++)
            seedStage.AddEntity(Id(seedBase + i), 0, ThroughputTypeId, null);
        await writer.ApplyAsync(NativeOnly(seedStage, ThroughputSrc, "tp-att-seed"));

        var attStage = IntentStage.New(totalRows);
        for (int i = 0; i < totalRows; i++)
        {
            Hash128 subj = Id(seedBase + i);
            Hash128 obj = Id(seedBase + totalRows + i);
            attStage.AddAttestation(
                Id(40_000_000 + i), subj, RelTypeId, obj, ThroughputSrc, null,
                (short)AttestationOutcome.Confirm, IntentStage.PgEpochUnixUs, 1L);
        }

        var sw = Stopwatch.StartNew();
        var result = await writer.ApplyAsync(NativeOnly(attStage, ThroughputSrc, "tp-att-native"));
        sw.Stop();

        Assert.Equal(totalRows, result.AttestationsInserted);
        Assert.InRange(result.RoundTrips, 1, IngestBaselineGates.MaxRoundTripsPerApplyBatch);
        double rowsPerSec = result.AttestationsInserted / sw.Elapsed.TotalSeconds;
        Assert.True(rowsPerSec >= IngestBaselineGates.MinWriterRowsPerSecond,
            $"Attestation apply {rowsPerSec:F0} rows/sec is below the {IngestBaselineGates.MinWriterRowsPerSecond:N0} gate "
            + $"({result.AttestationsInserted:N0} inserted in {sw.Elapsed.TotalSeconds:F2}s, round_trips={result.RoundTrips})");
    }

    [Fact]
    public async Task Physicality_NativeStage_Exceeds_500k_RowsPerSecond()
    {
        await EnsureVocabAsync();
        var writer = Writer(_pg.DataSource);

        const int totalRows = 500_000;
        int entBase = 60_000_000;

        var entStage = IntentStage.New(totalRows);
        for (int i = 0; i < totalRows; i++)
            entStage.AddEntity(Id(entBase + i), 2, ThroughputTypeId, null);
        await writer.ApplyAsync(NativeOnly(entStage, ThroughputSrc, "tp-phys-seed"));

        Span<double> coord = stackalloc double[4];
        coord[0] = 0.1; coord[1] = 0.2; coord[2] = 0.3; coord[3] = 0.4;
        var hilbert = Hilbert128.Encode(coord);
        var physStage = IntentStage.New(totalRows);
        for (int i = 0; i < totalRows; i++)
        {
            var entId = Id(entBase + i);
            physStage.AddPhysicality(
                Id(70_000_000 + i), entId, (short)PhysicalityType.Content,
                coord, hilbert,
                ReadOnlySpan<double>.Empty, 1, 0.0, 4, IntentStage.PgEpochUnixUs);
        }

        var sw = Stopwatch.StartNew();
        var result = await writer.ApplyAsync(NativeOnly(physStage, ThroughputSrc, "tp-phys-native"));
        sw.Stop();

        Assert.Equal(totalRows, result.PhysicalitiesInserted);
        Assert.InRange(result.RoundTrips, 1, IngestBaselineGates.MaxRoundTripsPerApplyBatch);
        double rowsPerSec = result.PhysicalitiesInserted / sw.Elapsed.TotalSeconds;
        Assert.True(rowsPerSec >= IngestBaselineGates.MinWriterRowsPerSecond,
            $"Physicality apply {rowsPerSec:F0} rows/sec is below the {IngestBaselineGates.MinWriterRowsPerSecond:N0} gate "
            + $"({result.PhysicalitiesInserted:N0} inserted in {sw.Elapsed.TotalSeconds:F2}s, round_trips={result.RoundTrips})");
    }

    [Fact]
    public async Task BulkApply_RoundTrips_StayConstantInRowCount()
    {
        // The Rule #8 lane: one GUC/lock batch, one verification probe per
        // 131072-id chunk, one COPY per touched table. 50k rows must not
        // cost more round trips than 5k rows.
        await EnsureVocabAsync();

        async Task<int> RoundTripsFor(int rows, int idBase)
        {
            var stage = IntentStage.New(rows);
            for (int i = 0; i < rows; i++)
                stage.AddEntity(Id(idBase + i), 0, ThroughputTypeId, null);
            var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
            var r = await writer.ApplyAsync(NativeOnly(stage, ThroughputSrc, $"tp-rt-{idBase}"));
            Assert.Equal(rows, r.EntitiesInserted);
            return r.RoundTrips;
        }

        int small = await RoundTripsFor(5_000, 80_000_000);
        int large = await RoundTripsFor(50_000, 81_000_000);
        Assert.Equal(small, large);
        Assert.InRange(large, 1, IngestBaselineGates.MaxRoundTripsPerApplyBatch);
    }
}
