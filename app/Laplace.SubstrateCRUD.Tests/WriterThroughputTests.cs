using System.Diagnostics;
using global::Npgsql;
using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// Write-path throughput gates (200k rows/sec). Tagged <c>Tier=perf</c> — excluded from CI
/// (<c>--filter Tier!=perf</c>) because they run against the live seeded substrate.
/// </summary>
[Trait("Tier", "perf")]
[Collection("substrate-pg")]
public sealed class WriterThroughputTests
{
    private readonly LocalPgFixture _pg;
    private readonly NpgsqlSubstrateWriter _writer;

    private static readonly Hash128 ThroughputSrc =
        Hash128.OfCanonical("substrate/source/test/throughput/v1");
    private static readonly Hash128 ThroughputTypeId =
        Hash128.OfCanonical("substrate/type/ThroughputFixture/v1");
    private static readonly Hash128 RelTypeId =
        Hash128.OfCanonical("substrate/type/ThroughputRelation/v1");

    public WriterThroughputTests(LocalPgFixture pg)
    {
        _pg = pg;
        _writer = new NpgsqlSubstrateWriter(pg.DataSource);
    }

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

    [Fact]
    public async Task Entity_Throughput_Exceeds_200k_RowsPerSecond()
    {
        await EnsureVocabAsync();

        const int totalRows = 500_000;
        const int batchSize = 50_000;
        const int batches   = totalRows / batchSize;

        long totalInserted = 0;
        var sw = Stopwatch.StartNew();

        for (int b = 0; b < batches; b++)
        {
            int base_ = b * batchSize + 10_000_000; // offset avoids collision with other tests
            var builder = new SubstrateChangeBuilder(ThroughputSrc, $"tp-ent/{b}", null,
                entityCapacity: batchSize, physicalityCapacity: 0, attestationCapacity: 0);
            for (int i = 0; i < batchSize; i++)
                builder.AddEntity(Id(base_ + i), 0, ThroughputTypeId);
            var result = await _writer.ApplyAsync(builder.Build());
            totalInserted += result.EntitiesInserted;
        }

        sw.Stop();
        double rowsPerSec = totalInserted / sw.Elapsed.TotalSeconds;
        Assert.True(rowsPerSec >= 200_000,
            $"Entity throughput {rowsPerSec:F0} rows/sec is below the 200k target "
          + $"({totalInserted:N0} inserted in {sw.Elapsed.TotalSeconds:F2}s)");
    }

    [Fact]
    public async Task Attestation_Throughput_Exceeds_200k_RowsPerSecond()
    {
        await EnsureVocabAsync();

        const int batchSize  = 50_000;
        const int batches    = 10;
        const int totalPairs = batchSize * batches;

        // Pre-seed subject and object entities (required for FK integrity in apply_batch)
        int seedBase = 20_000_000;
        {
            var seedBuilder = new SubstrateChangeBuilder(ThroughputSrc, "tp-att-seed", null,
                entityCapacity: totalPairs * 2, physicalityCapacity: 0, attestationCapacity: 0);
            for (int i = 0; i < totalPairs * 2; i++)
                seedBuilder.AddEntity(Id(seedBase + i), 0, ThroughputTypeId);
            await _writer.ApplyAsync(seedBuilder.Build());
        }

        long totalInserted = 0;
        var sw = Stopwatch.StartNew();

        for (int b = 0; b < batches; b++)
        {
            int base_ = seedBase + b * batchSize;
            var builder = new SubstrateChangeBuilder(ThroughputSrc, $"tp-att/{b}", null,
                entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: batchSize);
            for (int i = 0; i < batchSize; i++)
            {
                Hash128 subj = Id(base_ + i);
                Hash128 obj  = Id(base_ + totalPairs + i);
                builder.AddAttestation(new AttestationRow(
                    Id:                    Id(40_000_000 + b * batchSize + i),
                    SubjectId:             subj,
                    TypeId:                RelTypeId,
                    ObjectId:              obj,
                    SourceId:              ThroughputSrc,
                    ContextId:             null,
                    Outcome:               AttestationOutcome.Confirm,
                    LastObservedAtUnixUs:  IntentStage.PgEpochUnixUs,
                    ObservationCount:      1L,
                    ScoreFp1e9:            1_000_000_000L,
                    OpponentRdFp1e9:       0L));
            }
            var result = await _writer.ApplyAsync(builder.Build());
            totalInserted += result.AttestationsInserted;
        }

        sw.Stop();
        double rowsPerSec = totalInserted / sw.Elapsed.TotalSeconds;
        Assert.True(rowsPerSec >= 200_000,
            $"Attestation throughput {rowsPerSec:F0} rows/sec is below the 200k target "
          + $"({totalInserted:N0} inserted in {sw.Elapsed.TotalSeconds:F2}s)");
    }

    [Fact]
    public async Task Combined_Entity_Attestation_Throughput_Exceeds_200k_RowsPerSecond()
    {
        await EnsureVocabAsync();

        // Each batch: N entities + N attestations linking pairs → 2N rows
        const int batchSize   = 25_000;
        const int batches     = 10;
        const int rowsPerBatch = batchSize * 2;

        long totalEntities     = 0;
        long totalAttestations = 0;
        var sw = Stopwatch.StartNew();
        int base_ = 30_000_000;

        for (int b = 0; b < batches; b++)
        {
            int batchBase = base_ + b * batchSize * 2;
            var builder = new SubstrateChangeBuilder(ThroughputSrc, $"tp-combined/{b}", null,
                entityCapacity: batchSize * 2, physicalityCapacity: 0, attestationCapacity: batchSize);

            for (int i = 0; i < batchSize; i++)
            {
                Hash128 subj = Id(batchBase + i);
                Hash128 obj  = Id(batchBase + batchSize + i);
                builder.AddEntity(subj, 0, ThroughputTypeId);
                builder.AddEntity(obj,  0, ThroughputTypeId);
                builder.AddAttestation(new AttestationRow(
                    Id:                    Id(50_000_000 + b * batchSize + i),
                    SubjectId:             subj,
                    TypeId:                RelTypeId,
                    ObjectId:              obj,
                    SourceId:              ThroughputSrc,
                    ContextId:             null,
                    Outcome:               AttestationOutcome.Confirm,
                    LastObservedAtUnixUs:  IntentStage.PgEpochUnixUs,
                    ObservationCount:      1L,
                    ScoreFp1e9:            1_000_000_000L,
                    OpponentRdFp1e9:       0L));
            }

            var result = await _writer.ApplyAsync(builder.Build());
            totalEntities     += result.EntitiesInserted;
            totalAttestations += result.AttestationsInserted;
        }

        sw.Stop();
        long totalRows = totalEntities + totalAttestations;
        double rowsPerSec = totalRows / sw.Elapsed.TotalSeconds;
        Assert.True(rowsPerSec >= 200_000,
            $"Combined throughput {rowsPerSec:F0} rows/sec is below the 200k target "
          + $"({totalRows:N0} rows in {sw.Elapsed.TotalSeconds:F2}s, "
          + $"entities={totalEntities:N0} attestations={totalAttestations:N0})");
    }
}
