using System.Collections.Immutable;
using System.Reflection;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>Actual installed-extension acceptance through the normal evidence/consensus writers.</summary>
[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class PhysicalityObservationWriterTests(LocalPgFixture pg)
{
    private static readonly Hash128 HasPhysicality = RelationTypeRegistry.Resolve("HAS_PHYSICALITY").Id;
    private sealed record Input(Hash128 Source, EntityRow Entity, PhysicalityRow Winner, PhysicalityRow Alternate)
    {
        public SubstrateChange Change(string unit, bool prior = true)
        {
            var b = new SubstrateChangeBuilder(Source, unit).AddEntity(Entity)
                .AddPhysicality(Winner).AddPhysicality(Alternate);
            if (prior) b.DeclareSourcePrior(SourceTrust.StructuredCorpus);
            return b.Build();
        }
    }

    private static Input ComposeInput()
    {
        CodepointPerfcache.LoadDefault();
        string scope = Guid.NewGuid().ToString("N");
        var source = SubstrateCanonicalIds.Source($"PhysicalityWriterTest-{scope}");
        string text = "physicality-writer-" + scope;
        var components = text.Select(c =>
        {
            var a = CodepointPerfcache.Records[c];
            return new OrderedCompositionComponent(a.Hash, 0, a.CoordX, a.CoordY, a.CoordZ, a.CoordM, c, true);
        }).ToArray();
        var type = EntityTypeRegistry.SourceReference;
        var result = Assert.Single(OrderedComposition.ComposeBatch([
            new OrderedCompositionRequest(components, type, source, IntentStage.PgEpochUnixUs + 1_000_000)]));
        double[] trajectory = Trajectory.Build(components.Select(c => c.Id).ToArray());
        var winner = new PhysicalityRow(PhysicalityId.Compute(result.Id, PhysicalityType.Content),
            result.Id, source, PhysicalityType.Content,
            result.CoordX, result.CoordY, result.CoordZ, result.CoordM, result.Hilbert,
            trajectory, components.Length, null, null, IntentStage.PgEpochUnixUs + 1_000_000);
        // An explicitly supplied alternative observation of the exact same content.
        // Its coordinates are not claimed to be a second ordinary-composer result.
        double[] coordinate = [result.CoordX == 0 ? 0.125 : result.CoordX * 0.9,
            result.CoordY * 0.9, result.CoordZ * 0.9, result.CoordM * 0.9];
        var alternate = winner with { CoordX = coordinate[0], CoordY = coordinate[1],
            CoordZ = coordinate[2], CoordM = coordinate[3], HilbertIndex = Hilbert128.Encode(coordinate),
            ObservedAtUnixUs = winner.ObservedAtUnixUs + 1 };
        return new(source, new EntityRow(result.Id, result.Tier, type, source), winner, alternate);
    }

    private async Task DeclareSourceAsync(Input input)
    {
        await new NpgsqlSubstrateWriter(pg.DataSource).ApplyAsync(
            new SubstrateChangeBuilder(input.Source, "declare-test-source")
                .AddEntity(input.Source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId).Build());
    }

    private sealed record Evidence(string Id, string Descriptor, string Context, long Observations);
    private async Task<Evidence[]> EvidenceAsync(Input input)
    {
        await using var command = pg.DataSource.CreateCommand("""
            SELECT encode(id,'hex'),encode(object_id,'hex'),encode(context_id,'hex'),observation_count
            FROM laplace.attestations WHERE subject_id=$1 AND type_id=$2 AND source_id=$3
            ORDER BY id
            """);
        command.Parameters.AddWithValue(input.Entity.Id.ToBytes());
        command.Parameters.AddWithValue(HasPhysicality.ToBytes());
        command.Parameters.AddWithValue(input.Source.ToBytes());
        var rows = new List<Evidence>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        return rows.ToArray();
    }

    private async Task<string[]> ViewIdsAsync(string[] descriptors, Hash128 generatedSource)
    {
        await using var command = pg.DataSource.CreateCommand("""
            WITH tag AS (SELECT root_id FROM converse.text_root_placements(ARRAY['PhysicalityViewV1']))
            SELECT DISTINCT encode(p.entity_id,'hex')
            FROM laplace.entities e JOIN laplace.physicalities p ON p.entity_id=e.id AND p.type=1
            WHERE e.first_observed_by=$1
              AND EXISTS (SELECT FROM public.laplace_trajectory_constituents(p.trajectory) c,tag
                          WHERE c.ordinal=1 AND c.entity_id=tag.root_id)
              AND EXISTS (SELECT FROM public.laplace_trajectory_constituents(p.trajectory) c
                          WHERE c.ordinal=2 AND c.entity_id=ANY($2::bytea[]))
            ORDER BY 1
            """);
        command.Parameters.AddWithValue(generatedSource.ToBytes());
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
            descriptors.Select(Convert.FromHexString).ToArray());
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result.ToArray();
    }

    private async Task AssertBodiesExistAsync(IEnumerable<string> ids)
    {
        byte[][] selected = ids.Distinct().Select(Convert.FromHexString).ToArray();
        await using var command = pg.DataSource.CreateCommand("""
            SELECT count(DISTINCT e.id),count(DISTINCT p.id)
            FROM laplace.entities e JOIN laplace.physicalities p ON p.entity_id=e.id AND p.type=1
            WHERE e.id=ANY($1::bytea[])
            """);
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, selected);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(selected.LongLength, reader.GetInt64(0));
        Assert.Equal(selected.LongLength, reader.GetInt64(1));
    }

    private async Task<(string Descriptor, long Witnesses)[]> ConsensusAsync(Input input)
    {
        await using var command = pg.DataSource.CreateCommand("""
            SELECT encode(object_id,'hex'),witness_count FROM laplace.consensus
            WHERE subject_id=$1 AND type_id=$2 ORDER BY object_id
            """);
        command.Parameters.AddWithValue(input.Entity.Id.ToBytes());
        command.Parameters.AddWithValue(HasPhysicality.ToBytes());
        var rows = new List<(string, long)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetInt64(1)));
        return rows.ToArray();
    }

    [Fact]
    public async Task OrdinaryWriterRetainsBothRawFormsAndReusesDurableDescriptorViewEvidence()
    {
        var input = ComposeInput();
        await DeclareSourceAsync(input);
        var writer = new NpgsqlSubstrateWriter(pg.DataSource, durability: PostgresWriteDurability.Synchronous);
        var change = input.Change("first-unit");
        Assert.Single(change.Physicalities);
        Assert.Equal(2, change.PhysicalityObservations.Length);
        var first = await writer.ApplyWorkingSetAsync(change);
        var receipt = Assert.IsType<PhysicalityAdmissionReceipt>(first.PhysicalityAdmission);
        Assert.Equal(2, receipt.SourceForms);
        Assert.True(Assert.IsType<PostgresCommitReceipt>(first.PostgresCommit).LocalWalFlushAcknowledged);
        var evidence = await EvidenceAsync(input);
        Assert.Equal(2, evidence.Length);
        Assert.All(evidence, e => Assert.Equal(1, e.Observations));
        string[] descriptors = evidence.Select(e => e.Descriptor).Distinct().Order().ToArray();
        Assert.Equal(2, descriptors.Length);
        string[] views = await ViewIdsAsync(descriptors, receipt.GeneratedSourceId);
        Assert.Equal(2, views.Length);
        await AssertBodiesExistAsync(descriptors.Concat(views));
        var replay = await writer.ApplyWorkingSetAsync(input.Change("first-unit"));
        Assert.True(replay.JournalReplayHit);
        Assert.Equal(0, replay.AttestationsInserted);
        Assert.Equal(evidence, await EvidenceAsync(input));
        Assert.Equal(views, await ViewIdsAsync(descriptors, receipt.GeneratedSourceId));
        await AssertBodiesExistAsync(descriptors.Concat(views));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 2, 2)]
    [InlineData(2, 3, 2)]
    public async Task SupplementalRawRowsCannotExcludeSelectedBodiesOrDuplicateTheirWitness(
        int variant, int transportedForms, int expectedWitnesses)
    {
        var input = ComposeInput();
        await DeclareSourceAsync(input);
        SubstrateChange Change()
        {
            var change = input.Change("supplemental-raw");
            return change with { PhysicalityObservations = variant switch
            {
                0 => ImmutableArray<PhysicalityRow>.Empty,
                1 => [input.Alternate],
                // A separate equivalent object still reaches the native body
                // owner. CLR reference equality must not define evidence identity.
                _ => [input.Winner with { }, input.Alternate],
            } };
        }
        var writer = new NpgsqlSubstrateWriter(pg.DataSource);
        var first = await writer.ApplyWorkingSetAsync(Change());
        Assert.Equal(transportedForms,
            Assert.IsType<PhysicalityAdmissionReceipt>(first.PhysicalityAdmission).SourceForms);
        await PhysicalityWriterTestSupport.AssertSelectedRowsAsync(pg.DataSource,
            [input.Entity.Id], [input.Winner.Id], []);
        var evidence = await EvidenceAsync(input);
        Assert.Equal(expectedWitnesses, evidence.Length);
        Assert.All(evidence, row => Assert.Equal(1, row.Observations));
        Assert.True((await writer.ApplyWorkingSetAsync(Change())).JournalReplayHit);
        Assert.Equal(evidence, await EvidenceAsync(input));
    }

    [Fact]
    public async Task ConsensusFoldsGeneratedEvidenceOncePerDistinctActualSourceUnit()
    {
        var input = ComposeInput();
        await DeclareSourceAsync(input);
        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource, durability: PostgresWriteDurability.Synchronous), pg.DataSource);
        int callbacks = 0;
        ValueTask Verify(CancellationToken _) { callbacks++; return ValueTask.CompletedTask; }
        var first = await writer.ApplyWorkingSetAsync([input.Change("unit-one")], Verify);
        Assert.NotNull(first.PhysicalityAdmission);
        var firstEvidence = await EvidenceAsync(input);
        var firstConsensus = await ConsensusAsync(input);
        Assert.Equal(2, firstEvidence.Length);
        Assert.Equal(2, firstConsensus.Length);
        Assert.All(firstConsensus, row => Assert.Equal(1, row.Witnesses));
        Assert.Equal(1, callbacks);
        Assert.True((await writer.ApplyWorkingSetAsync([input.Change("unit-one")], Verify)).JournalReplayHit);
        Assert.Equal(firstEvidence, await EvidenceAsync(input));
        Assert.Equal(firstConsensus, await ConsensusAsync(input));
        Assert.Equal(1, callbacks);
        var next = await writer.ApplyWorkingSetAsync([input.Change("unit-two")], Verify);
        Assert.False(next.JournalReplayHit);
        var nextEvidence = await EvidenceAsync(input);
        Assert.Equal(4, nextEvidence.Length);
        Assert.Equal(2, nextEvidence.Select(e => e.Context).Distinct().Count());
        Assert.Equal(firstEvidence.Select(e => e.Descriptor).Order().ToArray(),
            nextEvidence.Select(e => e.Descriptor).Distinct().Order().ToArray());
        Assert.All(nextEvidence, row => Assert.Equal(1, row.Observations));
        var nextConsensus = await ConsensusAsync(input);
        Assert.Equal(2, nextConsensus.Length);
        Assert.All(nextConsensus, row => Assert.Equal(2, row.Witnesses));
        Assert.Equal(2, callbacks);
        Assert.True((await writer.ApplyWorkingSetAsync([input.Change("unit-two")], Verify)).JournalReplayHit);
        Assert.Equal(nextEvidence, await EvidenceAsync(input));
        Assert.Equal(nextConsensus, await ConsensusAsync(input));
        Assert.Equal(2, callbacks);
    }

    [Fact]
    public async Task SourceOnlyJournalBackfillRequiresFreshVerificationAndAtomicGeneratedEvidence()
    {
        var input = ComposeInput();
        await DeclareSourceAsync(input);
        var change = input.Change("historical-unit");
        Hash128 historicalToken = await SeedHistoricalSourceOnlyAsync(change);
        Assert.Empty(await EvidenceAsync(input));
        Assert.Empty(await ConsensusAsync(input));
        await using var writer = new ConsensusAccumulatingWriter(new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource);
        int callbacks = 0;
        bool reject = true;
        ValueTask VerifyBackfill(CancellationToken _)
        {
            callbacks++;
            if (reject) throw new InvalidDataException("controlled physicality source verification failure");
            return ValueTask.CompletedTask;
        }
        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => writer.ApplyWorkingSetAsync([change], VerifyBackfill));
        Assert.Equal("controlled physicality source verification failure", failure.Message);
        Assert.Empty(await EvidenceAsync(input));
        Assert.Empty(await ConsensusAsync(input));
        await using (var journal = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.ingest_flush_journal WHERE source_id=$1"))
        {
            journal.Parameters.AddWithValue(input.Source.ToBytes());
            Assert.Equal(1L, (long)(await journal.ExecuteScalarAsync())!);
        }
        // Existing detached COPY semantics can retain identity-only E/P rows;
        // evidence, consensus and augmented acceptance must remain atomic.
        reject = false;
        var backfill = await writer.ApplyWorkingSetAsync([input.Change("historical-unit")], VerifyBackfill);
        Assert.NotNull(backfill.PhysicalityAdmission);
        Assert.Equal(2, callbacks);
        var evidence = await EvidenceAsync(input);
        var consensus = await ConsensusAsync(input);
        Assert.Equal(2, evidence.Length);
        Assert.Equal(2, consensus.Length);
        Assert.All(evidence, row => Assert.Equal(1, row.Observations));
        Assert.All(consensus, row => Assert.Equal(1, row.Witnesses));
        Assert.True((await writer.ApplyWorkingSetAsync([input.Change("historical-unit")], VerifyBackfill)).JournalReplayHit);
        Assert.Equal(2, callbacks);
        Assert.Equal(evidence, await EvidenceAsync(input));
        Assert.Equal(consensus, await ConsensusAsync(input));
        await using var retained = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.ingest_flush_journal WHERE working_set_id=$1");
        retained.Parameters.AddWithValue(historicalToken.ToBytes());
        Assert.Equal(1L, (long)(await retained.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SourceOnlyConversationBackfillDoesNotAppendTheOriginalTurnAgain()
    {
        var input = ComposeInput();
        await DeclareSourceAsync(input);
        var session = Hash128.OfCanonical($"physicality-backfill-session/{Guid.NewGuid():N}");
        SubstrateChange Change()
        {
            var change = input.Change("historical-conversation");
            return change with { Entities = change.Entities.Add(
                new EntityRow(session, EntityTier.Document, EntityTypeRegistry.SourceReference, input.Source)) };
        }
        await SeedHistoricalSourceOnlyAsync(Change());
        // Use the actual ordinary appender to establish the historical turn.
        await using (var append = pg.DataSource.CreateCommand(
            "SELECT converse.session_append_turns($1,ARRAY[$2]::bytea[],now())"))
        {
            append.Parameters.AddWithValue(session.ToBytes());
            append.Parameters.AddWithValue(input.Entity.Id.ToBytes());
            await append.ExecuteNonQueryAsync();
        }
        async Task AssertOneTurn()
        {
            await using var command = pg.DataSource.CreateCommand(
                "SELECT turn_id FROM converse.session_turn_ids($1,NULL) ORDER BY ordinal");
            command.Parameters.AddWithValue(session.ToBytes());
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(input.Entity.Id.ToBytes(), reader.GetFieldValue<byte[]>(0));
            Assert.False(await reader.ReadAsync());
        }
        await AssertOneTurn();
        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource);
        var backfill = await writer.ApplyConversationTurnAsync(Change(), session, [input.Entity.Id]);
        Assert.NotNull(backfill.PhysicalityAdmission);
        await AssertOneTurn();
        var evidence = await EvidenceAsync(input);
        var consensus = await ConsensusAsync(input);
        Assert.Equal(2, evidence.Length);
        Assert.Equal(2, consensus.Length);
        Assert.All(consensus, row => Assert.Equal(1, row.Witnesses));
        Assert.True((await writer.ApplyConversationTurnAsync(Change(), session, [input.Entity.Id])).JournalReplayHit);
        await AssertOneTurn();
        Assert.Equal(evidence, await EvidenceAsync(input));
        Assert.Equal(consensus, await ConsensusAsync(input));
    }

    // Controlled historical precondition: real native source E/P COPY bytes plus
    // the current source-only v2 token, before the new descriptor stage exists.
    // Reuse the existing token owner rather than duplicating its hashing law.
    private async Task<Hash128> SeedHistoricalSourceOnlyAsync(SubstrateChange change)
    {
        using var stage = IntentStage.New(2);
        foreach (var e in change.Entities) stage.AddEntity(e.Id, e.Tier, e.TypeId, e.FirstObservedBy);
        foreach (var p in change.Physicalities)
            stage.AddPhysicality(p.Id, p.EntityId, (short)p.Type,
                new double[] { p.CoordX, p.CoordY, p.CoordZ, p.CoordM }, p.HilbertIndex,
                p.TrajectoryXyzm, p.NConstituents, p.AlignmentResidual, p.SourceDim, p.ObservedAtUnixUs);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var workingSet = typeof(NpgsqlSubstrateWriter).GetMethod("WorkingSetToken", flags)
            ?? throw new MissingMethodException("normal working-set token owner is unavailable");
        var replay = typeof(NpgsqlSubstrateWriter).GetMethod("ReplayTokenV2", flags)
            ?? throw new MissingMethodException("normal semantic replay token owner is unavailable");
        var oldToken = (Hash128)workingSet.Invoke(null, new object[] { new[] { change } })!;
        var token = (Hash128)replay.Invoke(null, new object[] { oldToken, new[] { stage } })!;
        await using var connection = await pg.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var (table, name) in new[] { (IntentStageTable.Entities, "entities"),
                     (IntentStageTable.Physicalities, "physicalities") })
        {
            byte[] bytes = stage.EmitCopyBinary(table);
            await using var copy = await connection.BeginRawBinaryCopyAsync(
                $"COPY laplace.{name} ({IntentStage.CopyColumnList(table)}) FROM STDIN (FORMAT BINARY)");
            await copy.WriteAsync(bytes);
        }
        await using var journal = new NpgsqlCommand(
            "INSERT INTO laplace.ingest_flush_journal(working_set_id,source_id) VALUES($1,$2)", connection, transaction);
        journal.Parameters.AddWithValue(token.ToBytes());
        journal.Parameters.AddWithValue(change.Metadata.SourceId.ToBytes());
        await journal.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return token;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidRawMetadataIsRejectedBeforeOpeningTheDatabase(bool partialTrajectory)
    {
        var input = ComposeInput();
        var change = input.Change("invalid", prior: partialTrajectory) with
        {
            CanonicalNames = ["Physicality_Validation_Probe"],
        };
        if (partialTrajectory)
            change = change with { PhysicalityObservations =
                [input.Winner with { TrajectoryXyzm = new double[] { 1, 2, 3 } }] };
        // A real Npgsql data source with no usable server: a database attempt
        // would fail as NpgsqlException, not the exact producer-validation error.
        await using var unavailable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unavailable;Username=unavailable;Pooling=false;Timeout=1");
        var writer = new NpgsqlSubstrateWriter(unavailable);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ApplyWorkingSetAsync(change));
        Assert.Contains(partialTrajectory ? "partial trajectory vertex" : "has no declared prior", error.Message);
    }
}
