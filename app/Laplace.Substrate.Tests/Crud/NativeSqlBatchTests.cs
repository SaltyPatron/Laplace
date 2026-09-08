using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class NativeSqlBatchTests(LocalPgFixture pg)
{
    [Fact]
    public async Task ConversationWriterCommitsTheOrderedSessionManifest()
    {
        var source = Hash128.OfCanonical("catalog-conversation/source");
        var session = Hash128.OfCanonical("catalog-conversation/session");
        var turn = Hash128.OfCanonical("catalog-conversation/turn");
        var type = Hash128.OfCanonical("catalog-conversation/type");
        var change = new SubstrateChangeBuilder(source, "catalog-conversation")
            .AddEntity(new EntityRow(session, 3, type, source))
            .AddEntity(new EntityRow(turn, 2, type, source))
            .AddPhysicality(new PhysicalityRow(
                Id: Hash128.OfCanonical("catalog-conversation/placement"),
                EntityId: turn, SourceId: source, Type: PhysicalityType.Content,
                CoordX: 0.1, CoordY: 0.2, CoordZ: 0.3, CoordM: 0.4,
                HilbertIndex: default, TrajectoryXyzm: null, NConstituents: 0,
                AlignmentResidual: null, SourceDim: null,
                ObservedAtUnixUs: IntentStage.PgEpochUnixUs))
            .AddAttestation(new AttestationRow(
                Hash128.OfCanonical("catalog-conversation/evidence"), turn,
                Hash128.OfCanonical("catalog-conversation/relation"), null, source, null,
                AttestationOutcome.Confirm, IntentStage.PgEpochUnixUs, 1,
                1_000_000_000L, 30_000_000_000L))
            .Build();
        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource);
        await writer.ApplyConversationTurnAsync(change, session, new[] { turn, turn }, CancellationToken.None);
        await using var read = pg.DataSource.CreateCommand(
            "SELECT turn_id FROM converse.session_turn_ids($1,NULL) ORDER BY ordinal");
        read.Parameters.AddWithValue(NpgsqlDbType.Bytea, session.ToBytes());
        await using var reader = await read.ExecuteReaderAsync();
        var turns = new List<byte[]>();
        while (await reader.ReadAsync()) turns.Add(reader.GetFieldValue<byte[]>(0));
        Assert.Equal(2, turns.Count);
        Assert.All(turns, id => Assert.Equal(turn.ToBytes(), id));
    }

    [Fact]
    public async Task ForwardTurnBindsAllSixCatalogParameters()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        var rows = new List<NpgsqlSubstrateReads.WalkTextStepRow>();
        await foreach (var row in NpgsqlSubstrateReads.ForwardTurnStepsAsync(
            conn, "", null, 1, 1, 0, 1, CancellationToken.None))
            rows.Add(row);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task IndependentCatalogReadsKeepParameterAndResultPositions()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        byte[] first = Hash128.OfCanonical("catalog-batch/first").ToBytes();
        byte[] second = Hash128.OfCanonical("catalog-batch/second").ToBytes();
        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO laplace.entities(id,tier,type_id)
                VALUES($1,0,laplace.entity_type_id('Type')),($2,0,laplace.entity_type_id('Type'));
                """;
            setup.Parameters.AddWithValue(NpgsqlDbType.Bytea, first);
            setup.Parameters.AddWithValue(NpgsqlDbType.Bytea, second);
            await setup.ExecuteNonQueryAsync();
            setup.CommandText = "INSERT INTO laplace.canonical_names(id,name) VALUES($1,'First'),($2,'Second')";
            await setup.ExecuteNonQueryAsync();
        }
        var result = await NpgsqlRead.ReadBatchRowsAsync(conn,
            SqlCatalog.Get("entity.facets"),
            p => p.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { first }),
            r => r.GetFieldValue<byte[]>(0),
            SqlCatalog.Get("display.labels"),
            p => p.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { second, first, second }),
            r => r.GetString(1));
        Assert.Equal(first, Assert.Single(result.First));
        Assert.Equal(new[] { "Second", "First", "Second" }, result.Second);
        // The single-command route shares the same catalog contract, including
        // callers that still supply descriptive parameter names.
        var single = await NpgsqlRead.ReadRowsAsync(conn, SqlCatalog.Get("display.labels"),
            r => r.GetString(1),
            p => p.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Bytea, new[] { first, second, first }));
        Assert.Equal(new[] { "First", "Second", "First" }, single);
        await transaction.RollbackAsync();
    }
}
