using System.Buffers.Binary;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Model;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Tests.Model;

public sealed class ModelCircuitReadbackTests(ITestOutputHelper output)
{
    [SkippableFact]
    [Trait("Tier", "live")]
    public async Task SourceScopedReadback_MatchesWriterAndKeepsSameOrdinalSourcesSeparate()
    {
        string? connection = Environment.GetEnvironmentVariable("LAPLACE_MODEL_CORROBORATION_DB");
        Skip.If(string.IsNullOrEmpty(connection), "Select an existing database for model circuit readback.");
        if (!CodepointPerfcache.IsLoaded)
            CodepointPerfcache.Load(TestInstall.ResolvePerfcacheOrThrow());

        // These are isolated source/trajectory fixtures, not a claim that a
        // checkpoint was ingested. Exercise the production writer's identities
        // and native carrier against the actual SQL reader; roll everything back.
        Hash128[] sources = [Hash128.Text("circuit readback source A"), Hash128.Text("circuit readback source B")];
        Hash128[] tokens = [Hash128.Text("king"), Hash128.Text("queen")];
        await using var db = new NpgsqlConnection(connection!);
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            await using var identity = new NpgsqlCommand(
                "SELECT 'generation.model_circuit_testimony(bytea,text,integer,integer)'::regprocedure::oid::bigint",
                db, transaction);
            long originalOid = (long)(await identity.ExecuteScalarAsync())!;
            string source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "model_consensus.sql.in"));
            // Let PostgreSQL parse its BEGIN ATOMIC bodies. The driver's legacy
            // multi-statement splitter treats their inner semicolons as boundaries.
            await using (var command = new NpgsqlCommand(
                "SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='30s';\n" +
                "DO $readback_install$ BEGIN EXECUTE $readback_source$\n" + source +
                "\n$readback_source$; END $readback_install$;", db, transaction))
                await command.ExecuteNonQueryAsync();
            Assert.Equal(originalOid, (long)(await identity.ExecuteScalarAsync())!);

            var circuits = new Hash128[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                using var builder = new SubstrateChangeBuilder(sources[i], "circuit/readback");
                OrderedCompositionResult circuit = ModelCoordinates.StageCircuit(
                    builder, sources[i], $"source {i}", "attention", 3, 5);
                circuits[i] = circuit.Id;
                await using (var address = new NpgsqlCommand(
                    "SELECT generation.circuit_coord($1::bytea, ' Attention ', 3, 5)", db, transaction))
                {
                    address.Parameters.AddWithValue(sources[i].ToBytes());
                    Assert.Equal(circuit.Id.ToBytes(), (byte[])(await address.ExecuteScalarAsync())!);
                }

                Hash128[] ordered = i == 0 ? tokens : tokens.Reverse().ToArray();
                long[] scores = i == 0 ? [900_000_000, 700_000_000] : [800_000_000, 600_000_000];
                byte[] payload = TestimonyWalk.Pack(ordered, scores);
                // Independent ISO-WKB envelope around the production native
                // TestimonyWalk bytes; no reimplementation of its bit encoding.
                byte[] wkb = new byte[9 + payload.Length];
                wkb[0] = 1;
                BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(1), 3002);
                BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(5), (uint)ordered.Length);
                payload.CopyTo(wkb, 9);
                double[] coordinate = [circuit.CoordX, circuit.CoordY, circuit.CoordZ, circuit.CoordM];
                await using (var insert = new NpgsqlCommand("""
                    WITH inserted AS (
                        INSERT INTO laplace.entities(id,tier,type_id)
                        VALUES ($1,$2,$3) RETURNING id
                    )
                    INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,source_dim)
                    SELECT $4,inserted.id,3,public.ST_MakePoint($5,$6,$7,$8),$9,public.ST_GeomFromWKB($10),2,1
                    FROM inserted;
                    """, db, transaction))
                {
                    insert.Parameters.AddWithValue(circuit.Id.ToBytes());
                    insert.Parameters.AddWithValue((short)circuit.Tier);
                    insert.Parameters.AddWithValue(ModelCoordinates.CircuitTypeId.ToBytes());
                    insert.Parameters.AddWithValue(PhysicalityId.Compute(circuit.Id, PhysicalityType.Projection).ToBytes());
                    foreach (double value in coordinate) insert.Parameters.AddWithValue(value);
                    insert.Parameters.AddWithValue(Hilbert128.Encode(coordinate).ToByteArray());
                    insert.Parameters.AddWithValue(wkb);
                    await insert.ExecuteNonQueryAsync();
                }
            }
            Assert.NotEqual(circuits[0], circuits[1]);

            for (int i = 0; i < sources.Length; i++)
            {
                await using var query = new NpgsqlCommand("""
                    SELECT circuit_id, token, score_fp1e9, logical_ordinal
                    FROM generation.model_circuit_testimony($1, 'attention', 3, 5)
                    ORDER BY logical_ordinal
                    """, db, transaction);
                query.Parameters.AddWithValue(sources[i].ToBytes());
                await using var rows = await query.ExecuteReaderAsync();
                int ordinal = 0;
                while (await rows.ReadAsync())
                {
                    Assert.Equal(circuits[i].ToBytes(), rows.GetFieldValue<byte[]>(0));
                    Assert.Equal(tokens[i == 0 ? ordinal : 1 - ordinal].ToBytes(), rows.GetFieldValue<byte[]>(1));
                    Assert.Equal(i == 0 ? 900_000_000L - ordinal * 200_000_000L
                        : 800_000_000L - ordinal * 200_000_000L, rows.GetInt64(2));
                    Assert.Equal(ordinal, rows.GetInt32(3));
                    ordinal++;
                }
                Assert.Equal(2, ordinal);
            }

            await using (var invalid = new NpgsqlCommand("""
                SELECT generation.circuit_coord($1, 'embedding', NULL, NULL)
                         = generation.circuit_coord($1, 'embedding', -1, -1),
                       generation.circuit_coord($1, 'attention', NULL, 5) IS NULL,
                       generation.circuit_coord($1, 'attention', -1, 5) IS NULL,
                       NOT EXISTS (SELECT 1 FROM generation.model_circuit_testimony($1, 'attention', 3, 6)),
                       NOT EXISTS (SELECT 1 FROM generation.model_circuit_testimony(NULL::bytea, 'attention', 3, 5)),
                       generation.circuit_coord($1, 'embedding') = $2
                """, db, transaction))
            {
                invalid.Parameters.AddWithValue(sources[0].ToBytes());
                invalid.Parameters.AddWithValue(ModelCoordinates.CircuitId(
                    sources[0], "source 0", "embedding", -1, -1).ToBytes());
                await using var row = await invalid.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync());
                for (int i = 0; i < 6; i++) Assert.True(row.GetBoolean(i));
            }
            output.WriteLine($"database={db.Database}; sources=2; circuits=2; read_vertices=4; " +
                "writer_identity_matches=true; cross_source_rows=0; missing_head_rows=0; transaction=rollback");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }
}
