using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class NativeSqlBatchTests(LocalPgFixture pg)
{
    [Fact]
    public async Task WitnessScopesExcludeCrossProductsButRetainConflictingObjects()
    {
        var prefix = "witness-scope/" + Guid.NewGuid().ToString("N");
        Hash128 Id(string name) => Hash128.OfCanonical(prefix + "/" + name);
        var source = Id("source"); var otherSource = Id("other-source");
        var playing = Id("playing"); var line = Id("line"); var context = Id("context");
        var hasEvent = Id("has-event"); var hasResult = Id("has-result");
        var eventId = Id("event"); var resultId = Id("result"); var conflicting = Id("conflicting");
        AttestationRow Row(Hash128 subject, Hash128 type, Hash128 obj, Hash128 src, Hash128? ctx)
            => NativeAttestation.CategoricalResolved(subject, type, obj, src, ctx, SourceTrust.StructuredCorpus);
        var selectedEvent = Row(playing, hasEvent, eventId, source, null);
        var selectedResult = Row(line, hasResult, resultId, source, playing);
        var conflictingResult = Row(line, hasResult, conflicting, source, playing);
        var rows = new[] { selectedEvent, selectedResult, conflictingResult,
            Row(line, hasEvent, eventId, source, playing), // actual PGN header cross-product
            Row(line, hasResult, resultId, otherSource, playing),
            Row(line, hasResult, resultId, source, context),
            Row(line, hasResult, resultId, source, null) };
        var builder = new SubstrateChangeBuilder(source, prefix).DeclareSourcePrior(SourceTrust.StructuredCorpus);
        foreach (var id in new[] { source, otherSource, playing, line, context, hasEvent, hasResult, eventId, resultId, conflicting })
            builder.AddEntity(id, 0, id);
        foreach (var row in rows) builder.AddAttestation(row);
        var writer = new NpgsqlSubstrateWriter(pg.DataSource);
        await writer.ApplyAsync(builder.Build());
        NpgsqlAttestationReads.WitnessScope[] scopes =
        [new(playing, hasEvent, source, null), new(line, hasResult, source, playing), new(playing, hasEvent, source, null)];
        var actual = await NpgsqlAttestationReads.WitnessesAsync(pg.DataSource, scopes, CancellationToken.None);
        Assert.Equal(3, actual.Count);
        Assert.Equal<Hash128>(new[] { selectedEvent.Id, selectedResult.Id, conflictingResult.Id }.OrderBy(id => id.Hi).ThenBy(id => id.Lo),
            actual.Select(a => Hash128.FromBytes(a.Id)).OrderBy(id => id.Hi).ThenBy(id => id.Lo));
        Assert.Contains(actual, a => a.ContextId is null);
        Assert.Empty(await NpgsqlAttestationReads.WitnessesAsync(pg.DataSource, [], CancellationToken.None));
    }

    [Fact]
    public async Task ChessSearchBindsTextArrayAndBooleanThroughTheNativeCatalog()
    {
        var rows = await NpgsqlSubstrateReads.ChessPlayerSearchCandidatesAsync(
            pg.DataSource, ["catalog-no-such-player"], 1, CancellationToken.None,
            offset: 0, sort: "games", direction: "asc", exactOnly: true);
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationWriterResumesProjectionWithoutForgingContent(bool batchPrefix)
    {
        var tag = $"catalog-conversation/{batchPrefix}/{Guid.NewGuid():N}";
        var source = Hash128.OfCanonical($"{tag}/source");
        var session = Hash128.OfCanonical($"{tag}/session");
        var atoms = new[] { Hash128.OfCanonical($"{tag}/a"), Hash128.OfCanonical($"{tag}/b") };
        var turn = Hash128.Merkle(4, atoms);
        var type = Hash128.OfCanonical("catalog-conversation/type");
        var builder = new SubstrateChangeBuilder(source, tag)
            .DeclareSourcePrior(SourceTrust.StructuredCorpus)
            .AddEntity(new EntityRow(session, 4, type, source))
            .AddEntity(new EntityRow(turn, 4, type, source))
            .AddPhysicality(new PhysicalityRow(
                Id: PhysicalityId.Compute(turn, PhysicalityType.Content),
                EntityId: turn, SourceId: source, Type: PhysicalityType.Content,
                CoordX: 0.1, CoordY: 0.2, CoordZ: 0.3, CoordM: 0.4,
                HilbertIndex: Hilbert128.Encode([0.1, 0.2, 0.3, 0.4]),
                TrajectoryXyzm: Trajectory.Build(atoms,
                    [Trajectory.VertexFlags(0, false, 0), Trajectory.VertexFlags(0, false, 0)]),
                NConstituents: atoms.Length,
                AlignmentResidual: null, SourceDim: null,
                ObservedAtUnixUs: IntentStage.PgEpochUnixUs))
            .AddAttestation(new AttestationRow(
                Hash128.OfCanonical($"{tag}/evidence"), turn,
                Hash128.OfCanonical("catalog-conversation/relation"), null, source, null,
                AttestationOutcome.Confirm, IntentStage.PgEpochUnixUs, 1,
                1_000_000_000L, 30_000_000_000L));
        foreach (var atom in atoms)
        {
            builder.AddEntity(new EntityRow(atom, 0, type, source));
            builder.AddPhysicality(new PhysicalityRow(
                PhysicalityId.Compute(atom, PhysicalityType.Content), atom, source,
                PhysicalityType.Content, 0.1, 0.2, 0.3, 0.4, Hilbert128.Encode([0.1, 0.2, 0.3, 0.4]), null, 0,
                null, null, IntentStage.PgEpochUnixUs));
        }
        if (batchPrefix)
        {
            // Same governed Projection contract emitted by AgentTrace. The
            // native appender must retain the batch prefix even without flags.
            builder.AddPhysicality(new PhysicalityRow(
                PhysicalityId.Compute(session, PhysicalityType.Projection), session, source,
                PhysicalityType.Projection, 0.1, 0.2, 0.3, 0.4, Hilbert128.Encode([0.1, 0.2, 0.3, 0.4]),
                Trajectory.Build(new[] { turn }), 1, null, null, IntentStage.PgEpochUnixUs));
        }
        var change = builder.Build();
        await using (var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource))
        {
            await writer.ApplyConversationTurnAsync(change, session, new[] { turn, turn });
            await writer.ApplyConversationTurnAsync(change, session, new[] { turn, turn });
        }
        // Reopen the writer, append one new occurrence, and retain the earlier
        // exact order. Replaying the first journaled intent above adds nothing.
        var next = new SubstrateChangeBuilder(source, $"{tag}/next")
            .AddEntity(new EntityRow(session, 4, type, source)).Build();
        await using (var resumed = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource))
            await resumed.ApplyConversationTurnAsync(next, session, new[] { atoms[0] });

        await using var read = pg.DataSource.CreateCommand(
            "SELECT turn_id FROM converse.session_turn_ids($1,NULL) ORDER BY ordinal");
        read.Parameters.AddWithValue(NpgsqlDbType.Bytea, session.ToBytes());
        var turns = new List<byte[]>();
        await using (var reader = await read.ExecuteReaderAsync())
            while (await reader.ReadAsync()) turns.Add(reader.GetFieldValue<byte[]>(0));
        Assert.Equal(batchPrefix ? 4 : 3, turns.Count);
        Assert.All(turns.Take(turns.Count - 1), id => Assert.Equal(turn.ToBytes(), id));
        Assert.Equal(atoms[0].ToBytes(), turns[^1]);

        await using var proof = pg.DataSource.CreateCommand(
            """
            SELECT p.id,p.type,p.n_constituents,
                   (SELECT count(*) FROM laplace.physicalities WHERE entity_id=$1 AND type=1),
                   (SELECT public.laplace_hash128_merkle(0::smallint,
                       array_agg(c.entity_id ORDER BY c.ordinal))
                    FROM laplace.physicalities content,
                         public.laplace_trajectory_expanded_constituents(content.trajectory) c
                    WHERE content.entity_id=$2 AND content.type=1),
                   (SELECT count(*) FROM laplace.physicalities content
                    JOIN laplace.entities parent ON parent.id=content.entity_id,
                         public.laplace_trajectory_constituents(content.trajectory) c
                    WHERE content.entity_id=$2 AND content.type=1
                      AND realize.vertex_tier(c.flags)>=parent.tier),
                   (SELECT array_agg(realize.vertex_tier(c.flags) ORDER BY c.ordinal)
                    FROM public.laplace_trajectory_expanded_constituents(p.trajectory) c)
            FROM laplace.physicalities p WHERE p.entity_id=$1
            """);
        proof.Parameters.AddWithValue(NpgsqlDbType.Bytea, session.ToBytes());
        proof.Parameters.AddWithValue(NpgsqlDbType.Bytea, turn.ToBytes());
        await using var evidence = await proof.ExecuteReaderAsync();
        Assert.True(await evidence.ReadAsync());
        Assert.Equal(PhysicalityId.Compute(session, PhysicalityType.Projection).ToBytes(),
            evidence.GetFieldValue<byte[]>(0));
        Assert.Equal((short)PhysicalityType.Projection, evidence.GetInt16(1));
        Assert.Equal(turns.Count, evidence.GetInt32(2));
        Assert.Equal(0, evidence.GetInt64(3));
        Assert.Equal(turn.ToBytes(), evidence.GetFieldValue<byte[]>(4));
        Assert.Equal(0, evidence.GetInt64(5));
        var tiers = evidence.GetFieldValue<short[]>(6);
        Assert.All(tiers.Take(tiers.Length - 1), tier => Assert.Equal((short)4, tier));
        Assert.Equal((short)0, tiers[^1]);
        Assert.False(await evidence.ReadAsync());
    }

    [Fact]
    public async Task LegacySessionContentIsPreservedAndRequiresExplicitRecovery()
    {
        // A historical defect cannot be staged through the current writer.
        // Seed one legacy row directly in this disposable database fixture.
        var session = Hash128.OfCanonical($"legacy-session/{Guid.NewGuid():N}");
        var contentId = PhysicalityId.Compute(session, PhysicalityType.Content);
        try
        {
            await using (var connection = await pg.DataSource.OpenConnectionAsync())
            {
                await using var seed = new NpgsqlBatch(connection);
                var entity = new NpgsqlBatchCommand(
                    "INSERT INTO laplace.entities(id,tier,type_id) "
                    + "VALUES($1,4,laplace.entity_type_id('Conversation_Session'))");
                entity.Parameters.AddWithValue(NpgsqlDbType.Bytea, session.ToBytes());
                seed.BatchCommands.Add(entity);
                var content = new NpgsqlBatchCommand(
                    """
                    INSERT INTO laplace.physicalities
                        (id,entity_id,type,coord,hilbert_index,n_constituents)
                    VALUES($1,$2,1,public.ST_MakePoint(0,0,0,0),decode(repeat('00',16),'hex'),0)
                    """);
                content.Parameters.AddWithValue(NpgsqlDbType.Bytea, contentId.ToBytes());
                content.Parameters.AddWithValue(NpgsqlDbType.Bytea, session.ToBytes());
                seed.BatchCommands.Add(content);
                Assert.Equal(2, await seed.ExecuteNonQueryAsync());
            }
            await using var read = pg.DataSource.CreateCommand(
                "SELECT * FROM converse.session_turn_ids($1,NULL)");
            read.Parameters.AddWithValue(NpgsqlDbType.Bytea, session.ToBytes());
            var error = await Assert.ThrowsAsync<PostgresException>(async () =>
                { await using var rows = await read.ExecuteReaderAsync(); });
            Assert.Equal("22000", error.SqlState);
            Assert.Contains("legacy Content manifest requires typed recovery", error.MessageText);
            await using var append = pg.DataSource.CreateCommand(
                "SELECT converse.session_append_turns($1,ARRAY[$1],now())");
            append.Parameters.AddWithValue(NpgsqlDbType.Bytea, session.ToBytes());
            var appendError = await Assert.ThrowsAsync<PostgresException>(async () =>
                { await append.ExecuteScalarAsync(); });
            Assert.Equal("22000", appendError.SqlState);
            Assert.Contains("legacy Content manifest requires typed recovery", appendError.MessageText);
            await using var retained = pg.DataSource.CreateCommand(
                "SELECT id,type FROM laplace.physicalities WHERE entity_id=$1");
            retained.Parameters.AddWithValue(NpgsqlDbType.Bytea, session.ToBytes());
            await using var receipt = await retained.ExecuteReaderAsync();
            Assert.True(await receipt.ReadAsync());
            Assert.Equal(contentId.ToBytes(), receipt.GetFieldValue<byte[]>(0));
            Assert.Equal((short)PhysicalityType.Content, receipt.GetInt16(1));
            Assert.False(await receipt.ReadAsync());
        }
        finally
        {
            // This collection shares a database: retain the defect for the
            // assertions above, then remove only this fixture's unique session.
            await using var connection = await pg.DataSource.OpenConnectionAsync();
            await using var cleanup = new NpgsqlBatch(connection);
            foreach (string sql in new[]
            {
                "DELETE FROM laplace.physicalities WHERE entity_id=$1",
                "DELETE FROM laplace.entities WHERE id=$1",
            })
            {
                var command = new NpgsqlBatchCommand(sql);
                command.Parameters.AddWithValue(NpgsqlDbType.Bytea, session.ToBytes());
                cleanup.BatchCommands.Add(command);
            }
            await cleanup.ExecuteNonQueryAsync();
        }
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
