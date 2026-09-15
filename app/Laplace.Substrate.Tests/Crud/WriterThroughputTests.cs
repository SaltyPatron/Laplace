using Laplace.Decomposers.Abstractions;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using global::Npgsql;
using Microsoft.Extensions.Logging;
using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>Captures WS_APPLY phase lines so a failed 500k gate shows verify vs COPY.</summary>
file sealed class WsApplyCaptureLog : ILogger<NpgsqlSubstrateWriter>
{
    private readonly ConcurrentQueue<string> _lines = new();
    public string Join() => string.Join("\n", _lines);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        if (msg.Contains("WS_APPLY", StringComparison.Ordinal))
            _lines.Enqueue(msg);
    }
}

[Trait("Tier", "perf")]
[Collection("substrate-pg-writer-throughput")]
public sealed class EntityWriterThroughputTests
{
    private readonly LocalPgFixture _pg;

    private static readonly Hash128 ThroughputSrc =
        SubstrateCanonicalIds.OfVersioned("source", "test", "throughput-ent");
    private static readonly Hash128 ThroughputTypeId =
        Hash128.OfCanonical("ThroughputFixture");

    public EntityWriterThroughputTests(LocalPgFixture pg) => _pg = pg;

    private Hash128 Id(int seed) => Hash128.Blake3(BitConverter.GetBytes(seed));

    [Fact]
    public async Task NativeStage_Exceeds_500k_RowsPerSecond()
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "INSERT INTO laplace.entities (id, tier, type_id, first_observed_by) VALUES "
          + "($1, 0::smallint, $1, NULL) ON CONFLICT (id, tier) DO NOTHING");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, ThroughputTypeId.ToBytes());
        await cmd.ExecuteNonQueryAsync();

        var phases = new WsApplyCaptureLog();
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource, phases);
        const int totalRows = 500_000;
        var stage = IntentStage.New(totalRows);
        for (int i = 0; i < totalRows; i++)
            stage.AddEntity(Id(10_000_000 + i), 0, ThroughputTypeId, null);

        var change = WriterThroughputTests.NativeOnly(stage, ThroughputSrc, "tp-ent-native");
        // Bulk bracket matches IngestRunner; secondaries stay UP (O(tier) probes /
        // tier_type / GiST need them). Do NOT DropSecondaries here.
        await writer.BeginBulkRunAsync();
        var sw = Stopwatch.StartNew();
        var result = await writer.ApplyAsync(change);
        sw.Stop();
        await writer.CompleteBulkRunAsync();

        Assert.Equal(totalRows, result.EntitiesInserted);
        Assert.InRange(result.RoundTrips, 1, IngestBaselineGates.MaxRoundTripsPerApplyBatch);
        double rowsPerSec = result.EntitiesInserted / sw.Elapsed.TotalSeconds;
        Assert.True(rowsPerSec >= IngestBaselineGates.MinWriterRowsPerSecond,
            $"Entity apply {rowsPerSec:F0} rows/sec is below the {IngestBaselineGates.MinWriterRowsPerSecond:N0} gate "
          + $"({result.EntitiesInserted:N0} inserted in {sw.Elapsed.TotalSeconds:F2}s, round_trips={result.RoundTrips})\n"
          + phases.Join());
    }
}

[Trait("Tier", "perf")]
[Collection("substrate-pg-writer-throughput")]
public sealed class WriterThroughputTests
{
    private readonly LocalPgFixture _pg;

    private static readonly Hash128 ThroughputSrc =
        SubstrateCanonicalIds.OfVersioned("source", "test", "throughput");
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
          + "ON CONFLICT (id, tier) DO NOTHING");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, ThroughputTypeId.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, ThroughputSrc.ToBytes());
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bytea, RelTypeId.ToBytes());
        await cmd.ExecuteNonQueryAsync();
    }

    internal static SubstrateChange NativeOnly(
        IntentStage stage, Hash128 src, string unitName, long inputUnits = 0)
    {
        // This fixture explicitly owns every staged physicality; entity-only and
        // attestation-only stages have an empty source range.
        stage.RecordPhysicalitySourceSince(0, src);
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
            IntentStages: [stage]).WithSourcePrior(src, SourceTrust.StructuredCorpus);
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

        var attStage = IntentStage.New(totalRows);
        for (int i = 0; i < totalRows; i++)
        {
            Hash128 subj = Id(seedBase + i);
            Hash128 obj = Id(seedBase + totalRows + i);
            attStage.AddAttestation(
                Id(40_000_000 + i), subj, RelTypeId, obj, ThroughputSrc, null,
                (short)AttestationOutcome.Confirm, IntentStage.PgEpochUnixUs, 1L,
                sumScoreFp1e9: 1_000_000_000L, opponentRdFp1e9: 30_000_000_000L);
        }

        await writer.BeginBulkRunAsync();
        await writer.ApplyAsync(NativeOnly(seedStage, ThroughputSrc, "tp-att-seed"));
        var sw = Stopwatch.StartNew();
        var result = await writer.ApplyAsync(NativeOnly(attStage, ThroughputSrc, "tp-att-native"));
        sw.Stop();
        await writer.CompleteBulkRunAsync();

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
        CodepointPerfcache.LoadDefault();
        var entityIds = new Hash128[totalRows];
        var entStage = IntentStage.New(totalRows);
        var physStage = IntentStage.New(totalRows);
        // Real three-atom ordered compositions from the installed Unicode floor.
        // 95^3 distinct printable-ASCII sequences cover this fixture. Construction
        // is outside the timer; normal writer descriptor admission stays inside it.
        const int width = 4096;
        for (int start = 0; start < totalRows; start += width)
        {
            int count = Math.Min(width, totalRows - start);
            var requests = new OrderedCompositionRequest[count];
            for (int offset = 0; offset < count; offset++)
            {
                int ordinal = start + offset;
                int[] codepoints = [32 + ordinal / (95 * 95), 32 + ordinal / 95 % 95, 32 + ordinal % 95];
                var children = codepoints.Select(codepoint =>
                {
                    var atom = CodepointPerfcache.Records[codepoint];
                    return new OrderedCompositionComponent(atom.Hash, 0,
                        atom.CoordX, atom.CoordY, atom.CoordZ, atom.CoordM, checked((uint)codepoint), true);
                }).ToArray();
                requests[offset] = new OrderedCompositionRequest(children, ThroughputTypeId,
                    ThroughputSrc, IntentStage.PgEpochUnixUs);
            }
            var results = OrderedComposition.ComposeBatch(requests);
            Assert.Equal(count, results.Length);
            for (int offset = 0; offset < count; offset++)
            {
                var body = results[offset];
                entityIds[start + offset] = body.Id;
                entStage.AddEntity(body.Id, body.Tier, ThroughputTypeId, ThroughputSrc);
                physStage.AddPhysicality(PhysicalityId.Compute(body.Id, PhysicalityType.Content),
                    body.Id, (short)PhysicalityType.Content,
                    new double[] { body.CoordX, body.CoordY, body.CoordZ, body.CoordM }, body.Hilbert,
                    Trajectory.Build(requests[offset].Components.Select(c => c.Id).ToArray()),
                    3, null, null, IntentStage.PgEpochUnixUs);
            }
        }
        Assert.Equal(totalRows, entityIds.Distinct().Count());

        await writer.BeginBulkRunAsync();
        await writer.ApplyAsync(NativeOnly(entStage, ThroughputSrc, "tp-phys-seed"));
        var sw = Stopwatch.StartNew();
        var result = await writer.ApplyAsync(NativeOnly(physStage, ThroughputSrc, "tp-phys-native"));
        sw.Stop();
        await writer.CompleteBulkRunAsync();

        PhysicalityWriterTestSupport.AssertAttempts(result, 0, totalRows, 0, totalRows);
        await PhysicalityWriterTestSupport.AssertSelectedRowsAsync(_pg.DataSource,
            entityIds, entityIds.Select(id => PhysicalityId.Compute(id, PhysicalityType.Content)), []);
        Assert.InRange(result.RoundTrips, 1, IngestBaselineGates.MaxRoundTripsPerApplyBatch);
        // Count source physicalities, not the larger generated graph, in this rate.
        double rowsPerSec = totalRows / sw.Elapsed.TotalSeconds;
        Assert.True(rowsPerSec >= IngestBaselineGates.MinWriterRowsPerSecond,
            $"Physicality admission {rowsPerSec:F0} source forms/sec is below the {IngestBaselineGates.MinWriterRowsPerSecond:N0} gate "
            + $"({totalRows:N0} source forms; {result.PhysicalitiesInserted:N0} total P rows inserted "
            + $"in {sw.Elapsed.TotalSeconds:F2}s, logical_round_trips={result.RoundTrips})");
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

