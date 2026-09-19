using System.Reflection;
using global::Npgsql;
using NpgsqlTypes;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// Installed-extension acceptance for direct physicality provenance.
///
/// Ordinary ingestion stores the canonical typed physicality once and records
/// source/unit/time against that physicality id. Structural provenance is not
/// testimony and must not manufacture descriptor entities, descriptor
/// physicalities, HAS_PHYSICALITY attestations, or consensus.
/// </summary>
[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class PhysicalityObservationWriterTests(LocalPgFixture pg)
{
    private static readonly Hash128 HasPhysicality =
        RelationTypeRegistry.Resolve("HAS_PHYSICALITY").Id;

    private sealed record Input(
        Hash128 Source,
        EntityRow Entity,
        PhysicalityRow Winner,
        PhysicalityRow Alternate)
    {
        public SubstrateChange Change(string unit, bool prior = true)
        {
            var builder = new SubstrateChangeBuilder(Source, unit)
                .AddEntity(Entity)
                .AddPhysicality(Winner)
                .AddPhysicality(Alternate);
            if (prior) builder.DeclareSourcePrior(SourceTrust.StructuredCorpus);
            return builder.Build();
        }
    }

    private sealed record StructuralObservation(
        string Physicality,
        string SourceUnit,
        long ObservedAtUnixUs);

    private static Input ComposeInput()
    {
        CodepointPerfcache.LoadDefault();
        string scope = Guid.NewGuid().ToString("N");
        var source = SubstrateCanonicalIds.Source($"PhysicalityWriterTest-{scope}");
        string text = "physicality-writer-" + scope;
        var components = text.Select(c =>
        {
            var atom = CodepointPerfcache.Records[c];
            return new OrderedCompositionComponent(
                atom.Hash, 0, atom.CoordX, atom.CoordY, atom.CoordZ, atom.CoordM, c, true);
        }).ToArray();

        var type = EntityTypeRegistry.SourceReference;
        var result = Assert.Single(OrderedComposition.ComposeBatch([
            new OrderedCompositionRequest(
                components, type, source, IntentStage.PgEpochUnixUs + 1_000_000)
        ]));
        double[] trajectory = Trajectory.Build(components.Select(c => c.Id).ToArray());
        var winner = new PhysicalityRow(
            PhysicalityId.Compute(result.Id, PhysicalityType.Content),
            result.Id, source, PhysicalityType.Content,
            result.CoordX, result.CoordY, result.CoordZ, result.CoordM, result.Hilbert,
            trajectory, components.Length, null, null,
            IntentStage.PgEpochUnixUs + 1_000_000);

        // A second observed body for the same canonical typed placement exercises
        // provenance capture. It is not a second canonical physicality identity.
        double[] coordinate =
        [
            result.CoordX == 0 ? 0.125 : result.CoordX * 0.9,
            result.CoordY * 0.9,
            result.CoordZ * 0.9,
            result.CoordM * 0.9,
        ];
        var alternate = winner with
        {
            CoordX = coordinate[0],
            CoordY = coordinate[1],
            CoordZ = coordinate[2],
            CoordM = coordinate[3],
            HilbertIndex = Hilbert128.Encode(coordinate),
            ObservedAtUnixUs = winner.ObservedAtUnixUs + 1,
        };
        return new(
            source,
            new EntityRow(result.Id, result.Tier, type, source),
            winner,
            alternate);
    }

    private async Task DeclareSourceAsync(Input input)
    {
        await new NpgsqlSubstrateWriter(pg.DataSource).ApplyAsync(
            new SubstrateChangeBuilder(input.Source, "declare-test-source")
                .AddEntity(
                    input.Source,
                    EntityTier.Word,
                    BootstrapIntentBuilder.SourceTypeId)
                .Build());
    }

    private async Task<StructuralObservation[]> ObservationsAsync(Input input)
    {
        await using var command = pg.DataSource.CreateCommand("""
            SELECT encode(physicality_id,'hex'),
                   encode(source_unit_id,'hex'),
                   observed_at_unix_us
            FROM laplace.physicality_observations
            WHERE entity_id=$1 AND source_id=$2
            ORDER BY physicality_id,source_unit_id
            """);
        command.Parameters.AddWithValue(input.Entity.Id.ToBytes());
        command.Parameters.AddWithValue(input.Source.ToBytes());

        var rows = new List<StructuralObservation>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        return rows.ToArray();
    }

    private async Task AssertNoStructuralTestimonyAsync(Input input)
    {
        await using var command = pg.DataSource.CreateCommand("""
            SELECT
              (SELECT count(*) FROM laplace.attestations
               WHERE subject_id=$1 AND type_id=$2 AND source_id=$3),
              (SELECT count(*) FROM laplace.consensus
               WHERE subject_id=$1 AND type_id=$2)
            """);
        command.Parameters.AddWithValue(input.Entity.Id.ToBytes());
        command.Parameters.AddWithValue(HasPhysicality.ToBytes());
        command.Parameters.AddWithValue(input.Source.ToBytes());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.False(await reader.ReadAsync());
    }

    private static string Hex(Hash128 id) =>
        Convert.ToHexStringLower(id.ToBytes());

    [Fact]
    public async Task OrdinaryWriterRecordsDirectProvenanceWithoutGeneratedDescriptorRows()
    {
        var input = ComposeInput();
        await DeclareSourceAsync(input);
        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(
                pg.DataSource,
                durability: PostgresWriteDurability.Synchronous),
            pg.DataSource);

        var change = input.Change("first-unit");
        var selected = Assert.Single(change.Physicalities);
        Assert.Equal(input.Winner.Id, selected.Id);
        Assert.Equal(2, change.PhysicalityObservations.Length);

        var first = await writer.ApplyWorkingSetAsync(change);
        var receipt = Assert.IsType<PhysicalityAdmissionReceipt>(
            first.PhysicalityAdmission);

        Assert.Equal(2, receipt.SourceForms);
        Assert.Equal(2, receipt.PhysicalityObservationRows);
        Assert.Equal(1L, receipt.PhysicalityObservationWrites);
        Assert.Equal(0, receipt.GeneratedEntityRows);
        Assert.Equal(0, receipt.GeneratedPhysicalityRows);
        Assert.Equal(0, first.AttestationsInserted);
        Assert.Equal("canonical-merkle-physicality/v1", receipt.SnapshotReceipt);
        Assert.Equal(2, receipt.Forms.Length);
        Assert.All(receipt.Forms, form =>
        {
            Assert.Equal(selected.Id, form.PhysicalityId);
            Assert.True(form.ViewId.HasValue);
            Assert.Equal(selected.Id, form.ViewId.Value);
            Assert.Equal(PhysicalityViewState.Available, form.ViewState);
            Assert.Equal(0, form.MissingCount);
        });
        Assert.Empty(receipt.MissingViewReferences);

        var observations = await ObservationsAsync(input);
        var observation = Assert.Single(observations);
        Assert.Equal(Hex(selected.Id), observation.Physicality);
        Assert.Equal(Hex(change.Metadata.IntentId), observation.SourceUnit);
        Assert.Equal(
            Math.Max(input.Winner.ObservedAtUnixUs, input.Alternate.ObservedAtUnixUs),
            observation.ObservedAtUnixUs);

        await PhysicalityWriterTestSupport.AssertSelectedRowsAsync(
            pg.DataSource, [input.Entity.Id], [selected.Id], []);
        await PhysicalityWriterTestSupport.AssertPhysicalityReadbackAsync(
            pg.DataSource, selected.Id, selected);
        await AssertNoStructuralTestimonyAsync(input);

        var replay = await writer.ApplyWorkingSetAsync(input.Change("first-unit"));
        Assert.True(replay.JournalReplayHit);
        Assert.Equal(0, replay.EntitiesInserted);
        Assert.Equal(0, replay.PhysicalitiesInserted);
        Assert.Equal(0, replay.AttestationsInserted);
        Assert.Equal(observations, await ObservationsAsync(input));
        await AssertNoStructuralTestimonyAsync(input);
    }

    [Fact]
    public async Task DistinctSourceUnitsRemainDistinctStructuralObservationsWithoutConsensus()
    {
        var input = ComposeInput();
        await DeclareSourceAsync(input);
        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource),
            pg.DataSource);

        var one = input.Change("unit-one");
        var two = input.Change("unit-two");
        var first = await writer.ApplyWorkingSetAsync([one]);
        var second = await writer.ApplyWorkingSetAsync([two]);

        Assert.Equal(
            1L,
            Assert.IsType<PhysicalityAdmissionReceipt>(
                first.PhysicalityAdmission).PhysicalityObservationWrites);
        Assert.Equal(
            1L,
            Assert.IsType<PhysicalityAdmissionReceipt>(
                second.PhysicalityAdmission).PhysicalityObservationWrites);

        var observations = await ObservationsAsync(input);
        Assert.Equal(2, observations.Length);
        Assert.Equal(
            new[] { Hex(one.Metadata.IntentId), Hex(two.Metadata.IntentId) }.Order().ToArray(),
            observations.Select(row => row.SourceUnit).Order().ToArray());
        Assert.All(observations, row => Assert.Equal(Hex(input.Winner.Id), row.Physicality));
        await AssertNoStructuralTestimonyAsync(input);
    }

    [Fact]
    public async Task RawObservationSidecarCannotHideSelectedPhysicalityOrMintAnotherBody()
    {
        var input = ComposeInput();
        await DeclareSourceAsync(input);

        var original = input.Change("sidecar");
        var selected = Assert.Single(original.Physicalities);
        var change = original with { PhysicalityObservations = [input.Alternate] };
        var writer = new NpgsqlSubstrateWriter(pg.DataSource);

        var applied = await writer.ApplyWorkingSetAsync(change);
        var receipt = Assert.IsType<PhysicalityAdmissionReceipt>(
            applied.PhysicalityAdmission);

        Assert.Equal(2, receipt.SourceForms);
        Assert.Equal(2, receipt.PhysicalityObservationRows);
        Assert.Equal(1L, receipt.PhysicalityObservationWrites);
        Assert.Equal(0, receipt.GeneratedEntityRows);
        Assert.Equal(0, receipt.GeneratedPhysicalityRows);
        Assert.Equal(0, applied.AttestationsInserted);

        var observation = Assert.Single(await ObservationsAsync(input));
        Assert.Equal(Hex(selected.Id), observation.Physicality);
        await PhysicalityWriterTestSupport.AssertSelectedRowsAsync(
            pg.DataSource, [input.Entity.Id], [selected.Id], []);
        await PhysicalityWriterTestSupport.AssertPhysicalityReadbackAsync(
            pg.DataSource, selected.Id, selected);
        await AssertNoStructuralTestimonyAsync(input);
    }

    [Fact]
    public async Task SourceOnlyJournalBackfillRequiresFreshVerificationAndAddsOnlyProvenance()
    {
        var input = ComposeInput();
        await DeclareSourceAsync(input);
        var change = input.Change("historical-unit");
        Hash128 historicalToken = await SeedHistoricalSourceOnlyAsync(change);
        Assert.Empty(await ObservationsAsync(input));
        await AssertNoStructuralTestimonyAsync(input);

        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource),
            pg.DataSource);
        int callbacks = 0;
        bool reject = true;
        ValueTask VerifyBackfill(CancellationToken _)
        {
            callbacks++;
            if (reject)
                throw new InvalidDataException(
                    "controlled physicality source verification failure");
            return ValueTask.CompletedTask;
        }

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => writer.ApplyWorkingSetAsync([change], VerifyBackfill));
        Assert.Equal(
            "controlled physicality source verification failure",
            failure.Message);
        Assert.Empty(await ObservationsAsync(input));
        await AssertNoStructuralTestimonyAsync(input);

        reject = false;
        var backfill = await writer.ApplyWorkingSetAsync(
            [input.Change("historical-unit")],
            VerifyBackfill);
        var receipt = Assert.IsType<PhysicalityAdmissionReceipt>(
            backfill.PhysicalityAdmission);
        Assert.Equal(1L, receipt.PhysicalityObservationWrites);
        Assert.Single(await ObservationsAsync(input));
        Assert.Equal(0, receipt.GeneratedEntityRows);
        Assert.Equal(0, receipt.GeneratedPhysicalityRows);
        await AssertNoStructuralTestimonyAsync(input);

        Assert.True(
            (await writer.ApplyWorkingSetAsync(
                [input.Change("historical-unit")],
                VerifyBackfill)).JournalReplayHit);
        Assert.Equal(2, callbacks);

        await using var retained = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.ingest_flush_journal WHERE working_set_id=$1");
        retained.Parameters.AddWithValue(historicalToken.ToBytes());
        Assert.Equal(1L, (long)(await retained.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task SourceOnlyConversationBackfillDoesNotAppendOriginalTurnAgain()
    {
        var input = ComposeInput();
        await DeclareSourceAsync(input);
        var session = Hash128.OfCanonical(
            $"physicality-backfill-session/{Guid.NewGuid():N}");

        SubstrateChange Change()
        {
            var change = input.Change("historical-conversation");
            return change with
            {
                Entities = change.Entities.Add(
                    new EntityRow(
                        session,
                        EntityTier.Document,
                        EntityTypeRegistry.SourceReference,
                        input.Source))
            };
        }

        await SeedHistoricalSourceOnlyAsync(Change());
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
            Assert.Equal(
                input.Entity.Id.ToBytes(),
                reader.GetFieldValue<byte[]>(0));
            Assert.False(await reader.ReadAsync());
        }

        await AssertOneTurn();
        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource),
            pg.DataSource);
        var backfill = await writer.ApplyConversationTurnAsync(
            Change(), session, [input.Entity.Id]);

        var receipt = Assert.IsType<PhysicalityAdmissionReceipt>(
            backfill.PhysicalityAdmission);
        Assert.Equal(0, receipt.GeneratedEntityRows);
        Assert.Equal(0, receipt.GeneratedPhysicalityRows);
        Assert.Single(await ObservationsAsync(input));
        await AssertNoStructuralTestimonyAsync(input);
        await AssertOneTurn();

        Assert.True(
            (await writer.ApplyConversationTurnAsync(
                Change(), session, [input.Entity.Id])).JournalReplayHit);
        Assert.Single(await ObservationsAsync(input));
        await AssertOneTurn();
    }

    // Controlled historical precondition: real native source E/P COPY bytes plus
    // the historical source-only token, before direct structural provenance exists.
    private async Task<Hash128> SeedHistoricalSourceOnlyAsync(
        SubstrateChange change)
    {
        using var stage = IntentStage.New(2);
        foreach (var entity in change.Entities)
            stage.AddEntity(
                entity.Id,
                entity.Tier,
                entity.TypeId,
                entity.FirstObservedBy);
        foreach (var physicality in change.Physicalities)
            stage.AddPhysicality(
                physicality.Id,
                physicality.EntityId,
                (short)physicality.Type,
                [
                    physicality.CoordX,
                    physicality.CoordY,
                    physicality.CoordZ,
                    physicality.CoordM,
                ],
                physicality.HilbertIndex,
                physicality.TrajectoryXyzm,
                physicality.NConstituents,
                physicality.AlignmentResidual,
                physicality.SourceDim,
                physicality.ObservedAtUnixUs);

        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var workingSet = typeof(NpgsqlSubstrateWriter).GetMethod(
            "WorkingSetToken", flags)
            ?? throw new MissingMethodException(
                "normal working-set token owner is unavailable");
        var replay = typeof(NpgsqlSubstrateWriter).GetMethod(
            "ReplayTokenV2", flags)
            ?? throw new MissingMethodException(
                "normal semantic replay token owner is unavailable");
        var oldToken = (Hash128)workingSet.Invoke(
            null, new object[] { new[] { change } })!;
        var token = (Hash128)replay.Invoke(
            null, new object?[] { oldToken, new[] { stage }, null })!;

        await using var connection =
            await pg.DataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        foreach (var (table, name) in new[]
        {
            (IntentStageTable.Entities, "entities"),
            (IntentStageTable.Physicalities, "physicalities"),
        })
        {
            byte[] bytes = stage.EmitCopyBinary(table);
            await using var copy = await connection.BeginRawBinaryCopyAsync(
                $"COPY laplace.{name} ({IntentStage.CopyColumnList(table)}) FROM STDIN (FORMAT BINARY)");
            await copy.WriteAsync(bytes);
        }

        await using var journal = new NpgsqlCommand(
            "INSERT INTO laplace.ingest_flush_journal(working_set_id,source_id) VALUES($1,$2)",
            connection,
            transaction);
        journal.Parameters.AddWithValue(token.ToBytes());
        journal.Parameters.AddWithValue(
            change.Metadata.SourceId.ToBytes());
        await journal.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return token;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidRawMetadataIsRejectedBeforeOpeningDatabase(
        bool partialTrajectory)
    {
        var input = ComposeInput();
        var change = input.Change("invalid", prior: partialTrajectory) with
        {
            CanonicalNames = ["Physicality_Validation_Probe"],
        };
        if (partialTrajectory)
            change = change with
            {
                PhysicalityObservations =
                    [input.Winner with { TrajectoryXyzm = [1, 2, 3] }]
            };

        await using var unavailable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unavailable;Username=unavailable;Pooling=false;Timeout=1");
        var writer = new NpgsqlSubstrateWriter(unavailable);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.ApplyWorkingSetAsync(change));
        Assert.Contains(
            partialTrajectory
                ? "partial trajectory vertex"
                : "has no declared prior",
            error.Message);
    }
}
