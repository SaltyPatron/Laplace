using global::Npgsql;
using NpgsqlTypes;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Laplace.Decomposers.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class EntityInterpretationTransactionTests(LocalPgFixture pg, ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalSummaryAndInterpretationsCommitTogetherAfterReplicaRoleCopy(
        bool rejectFirstControl)
    {
        string scope = "interpretation-tx-" + Guid.NewGuid().ToString("N");
        Hash128 H(string value) => Hash128.OfCanonical(scope + "/" + value);
        var source = H("source");
        var firstType = H("type-a");
        var secondType = H("type-b");
        var alternateType = firstType.CompareToBytewise(secondType) < 0 ? firstType : secondType;
        var type = firstType.CompareToBytewise(secondType) < 0 ? secondType : firstType;
        int owners = NpgsqlSubstrateWriter.ApplyParallelism;
        var io = IngestSizing.ResolveApplyIo(owners);
        // An entity COPY row with a non-null source is 68 bytes. Use the actual
        // transport crossover so a parallel-capable host exercises detached COPY.
        int count = checked((int)Math.Max(2, (2L * io.CopyStartupBytes + 67) / 68));
        bool independentCopy = IngestSizing.ResolveCopyConnections(
            count, 68L * count, owners, io.CopyStartupBytes) > 1;
        var ids = Enumerable.Range(0, count).Select(i => H("entity/" + i)).ToArray();
        var bytes = ids.Select(id => id.ToBytes()).ToArray();
        using var builder = new SubstrateChangeBuilder(source, scope);
        foreach (var id in ids) builder.AddEntity(id, 3, type, source);
        // COPY selects tier3/typeHigh. The full interpretation summary is
        // tier3/typeLow, so its UPDATE must run after the canonical row exists.
        // Replica role leaves the ordinary INSERT trigger inactive.
        builder.AddEntity(ids[0], 5, alternateType, source);
        var change = builder.Build();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        async Task<long> CountAsync(
            string table, NpgsqlConnection? connection = null,
            NpgsqlTransaction? transaction = null, bool alternateOnly = false)
        {
            await using var command = connection is null
                ? pg.DataSource.CreateCommand()
                : connection.CreateCommand();
            command.Transaction = transaction;
            string key = table == "entities" ? "id" : "entity_id";
            command.CommandText = $"SELECT count(*) FROM laplace.{table} WHERE {key}=ANY($1::bytea[])"
                + (alternateOnly ? " AND tier=5 AND type_id=$2" : "");
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, bytes);
            if (alternateOnly) command.Parameters.AddWithValue(alternateType.ToBytes());
            return (long)(await command.ExecuteScalarAsync(stop.Token))!;
        }

        async Task<long> ReceiptCountAsync()
        {
            await using var command = pg.DataSource.CreateCommand(
                "SELECT count(*) FROM laplace.ingest_flush_journal WHERE source_id=$1");
            command.Parameters.AddWithValue(source.ToBytes());
            return (long)(await command.ExecuteScalarAsync(stop.Token))!;
        }


        async Task<Hash128> StoredTypeAsync(
            NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null)
        {
            await using var command = connection is null
                ? pg.DataSource.CreateCommand()
                : connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT type_id FROM laplace.entities WHERE id=$1";
            command.Parameters.AddWithValue(ids[0].ToBytes());
            var value = (byte[])(await command.ExecuteScalarAsync(stop.Token))!;
            return Hash128.FromBytes(value);
        }

        int participants = 0;
        async Task Participant(
            NpgsqlConnection connection, NpgsqlTransaction transaction,
            WorkingSetAcceptedEvidence _, CancellationToken ct)
        {
            participants++;
            Assert.Equal(count + 1L,
                await CountAsync("entity_interpretations", connection, transaction));
            Assert.Equal(1L,
                await CountAsync("entity_interpretations", connection, transaction, alternateOnly: true));
            Assert.Equal(alternateType, await StoredTypeAsync(connection, transaction));
            // The full sidecar and summary update belong to this control commit,
            // even when canonical COPY has already committed independently.
            Assert.Equal(0L, await CountAsync("entity_interpretations"));
            if (independentCopy)
            {
                Assert.Equal(count, await CountAsync("entities"));
                Assert.Equal(type, await StoredTypeAsync());
            }
            if (rejectFirstControl && participants == 1)
                throw new InvalidOperationException("controlled interpretation acceptance failure");
        }

        try
        {
            var writer = new NpgsqlSubstrateWriter(pg.DataSource,
                durability: PostgresWriteDurability.Synchronous);
            if (rejectFirstControl)
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    writer.ApplyWorkingSetAtomicAsync([change], Participant, null, stop.Token));
                Assert.Equal("controlled interpretation acceptance failure", failure.Message);
                Assert.Equal(0L, await CountAsync("entity_interpretations", alternateOnly: true));
                Assert.Equal(independentCopy ? count : 0L, await CountAsync("entities"));
                Assert.Equal(0L, await CountAsync("entity_interpretations"));
                if (independentCopy) Assert.Equal(type, await StoredTypeAsync());
                Assert.Equal(0L, await ReceiptCountAsync());
                // Retry through a fresh owner; committed canonical COPY rows stay
                // present, while the missing interpretation and receipt are admitted.
                writer = new NpgsqlSubstrateWriter(pg.DataSource,
                    durability: PostgresWriteDurability.Synchronous);
            }

            var accepted = await writer.ApplyWorkingSetAtomicAsync(
                [change], Participant, null, stop.Token);
            Assert.Equal(rejectFirstControl ? 2 : 1, participants);
            Assert.Equal(count, await CountAsync("entities"));
            Assert.Equal(count + 1L, await CountAsync("entity_interpretations"));
            Assert.Equal(alternateType, await StoredTypeAsync());
            Assert.Equal(1L, await CountAsync("entity_interpretations", alternateOnly: true));
            Assert.Equal(2L, await ReceiptCountAsync());
            if (independentCopy && !rejectFirstControl)
                Assert.True(accepted.CopyTransactionsCommitted > 1);
            Assert.Equal(accepted.CopyTransactionsStarted, accepted.CopyTransactionsCommitted);
            var replay = await writer.ApplyWorkingSetAtomicAsync([change], (_, _, _, _) =>
                throw new InvalidOperationException("complete replay must not invoke acceptance"),
                null, stop.Token);
            Assert.True(replay.JournalReplayHit);
            Assert.Equal(0, replay.EntitiesInserted);
            Assert.Equal(count + 1L, await CountAsync("entity_interpretations"));
            output.WriteLine(
                $"interpretation transaction proof: independent_copy={independentCopy}; "
                + $"rows={count}; owners={owners}; committed_copy_transactions={accepted.CopyTransactionsCommitted}; "
                + $"rejected_first_control={rejectFirstControl}");
        }
        finally
        {
            // Only this fixture's exact random source and entity IDs are removed.
            await using var cleanup = pg.DataSource.CreateCommand("""
                WITH owners AS (
                    DELETE FROM laplace.ingest_flush_journal_sources s
                    USING laplace.ingest_flush_journal j
                    WHERE s.working_set_id=j.working_set_id AND j.source_id=$1),
                receipts AS (
                    DELETE FROM laplace.ingest_flush_journal WHERE source_id=$1),
                interpretations AS (
                    DELETE FROM laplace.entity_interpretations WHERE entity_id=ANY($2::bytea[]))
                DELETE FROM laplace.entities WHERE id=ANY($2::bytea[])
                """);
            cleanup.Parameters.AddWithValue(source.ToBytes());
            cleanup.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, bytes);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FacetUpgradeRetainsObservationIdentityAndSemanticAcceptance(bool legacyV2Only)
    {
        string scope = "interpretation-replay-" + Guid.NewGuid().ToString("N");
        Hash128 H(string value) => Hash128.OfCanonical(scope + "/" + value);
        var source = H("source");
        var id = H("content");
        var type = H("type");
        var extraType = H("extra-type");
        var relation = H("relation");
        var witness = NativeAttestation.CategoricalResolved(id, relation, null, source, null, 1.0);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var originalBuilder = new SubstrateChangeBuilder(source, scope);
        originalBuilder.AddEntity(id, 3, type, source).AddAttestation(witness);
        var original = originalBuilder.Build();
        using var upgradedBuilder = new SubstrateChangeBuilder(source, scope);
        upgradedBuilder.AddEntity(id, 3, type, source).AddEntity(id, 7, extraType, source)
            .AddAttestation(witness);
        var upgraded = upgradedBuilder.Build();
        Assert.Equal(original.Metadata.IntentId, upgraded.Metadata.IntentId);
        var writer = new NpgsqlSubstrateWriter(pg.DataSource,
            durability: PostgresWriteDurability.Synchronous);
        int semanticAcceptances = 0, callbacks = 0;
        async Task Participant(NpgsqlConnection connection, NpgsqlTransaction transaction,
            WorkingSetAcceptedEvidence accepted, CancellationToken ct)
        {
            callbacks++;
            if (!accepted.OriginalReplay) semanticAcceptances++;
            if (callbacks == 1) Assert.Contains(witness.Id, accepted.AttestationIds);
            else
            {
                Assert.True(accepted.OriginalReplay);
                Assert.Empty(accepted.AttestationIds);
            }
            await Task.CompletedTask;
        }
        async Task<string> EvidenceAsync()
        {
            await using var command = pg.DataSource.CreateCommand("""
                SELECT COALESCE(jsonb_agg(to_jsonb(a) ORDER BY a.id)::text,'[]')
                FROM laplace.attestations a WHERE a.source_id=$1
                """);
            command.Parameters.AddWithValue(source.ToBytes());
            return (string)(await command.ExecuteScalarAsync(stop.Token))!;
        }
        async Task<long> FacetsAsync()
        {
            await using var command = pg.DataSource.CreateCommand(
                "SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id=$1");
            command.Parameters.AddWithValue(id.ToBytes());
            return (long)(await command.ExecuteScalarAsync(stop.Token))!;
        }
        try
        {
            var fresh = await writer.ApplyWorkingSetAtomicAsync([original], Participant, null, stop.Token);
            Assert.Equal(1, fresh.EntitiesInserted);
            Assert.Equal(1, fresh.AttestationsInserted);
            Assert.Equal(1, semanticAcceptances);
            var evidence = await EvidenceAsync();
            Assert.NotEqual("[]", evidence);
            if (legacyV2Only)
            {
                // Retain the real accepted V2 semantic receipt and evidence, then
                // remove only this fixture's new-format receipt and sidecar. This
                // is the pre-sidecar state an upgrade must reconcile without votes.
                var tokens = new List<Hash128>();
                await using (var command = pg.DataSource.CreateCommand(
                    "SELECT working_set_id FROM laplace.ingest_flush_journal WHERE source_id=$1"))
                {
                    command.Parameters.AddWithValue(source.ToBytes());
                    await using var reader = await command.ExecuteReaderAsync(stop.Token);
                    while (await reader.ReadAsync(stop.Token))
                        tokens.Add(Hash128.FromBytes(reader.GetFieldValue<byte[]>(0)));
                }
                Assert.Equal(2, tokens.Count);
                var semantic = Assert.Single(tokens.Where(token => tokens.Contains(
                    NpgsqlSubstrateWriter.InterpretationReplayToken(token, original.EntityInterpretations))));
                var full = NpgsqlSubstrateWriter.InterpretationReplayToken(semantic, original.EntityInterpretations);
                await using var oldShape = pg.DataSource.CreateCommand("""
                    WITH owner AS (
                        DELETE FROM laplace.ingest_flush_journal_sources WHERE working_set_id=$1),
                    receipt AS (
                        DELETE FROM laplace.ingest_flush_journal WHERE working_set_id=$1)
                    DELETE FROM laplace.entity_interpretations WHERE entity_id=$2
                    """);
                oldShape.Parameters.AddWithValue(full.ToBytes());
                oldShape.Parameters.AddWithValue(id.ToBytes());
                await oldShape.ExecuteNonQueryAsync(stop.Token);
                Assert.Equal(0, await FacetsAsync());
            }
            writer = new NpgsqlSubstrateWriter(pg.DataSource,
                durability: PostgresWriteDurability.Synchronous);
            var upgrade = await writer.ApplyWorkingSetAtomicAsync([upgraded], Participant, null, stop.Token);
            Assert.False(upgrade.JournalReplayHit);
            Assert.Equal(0, upgrade.EntitiesInserted);
            Assert.Equal(0, upgrade.AttestationsInserted);
            Assert.Equal(1, semanticAcceptances);
            Assert.Equal(2, callbacks);
            Assert.Equal(2, await FacetsAsync());
            Assert.Equal(evidence, await EvidenceAsync());
            var replay = await writer.ApplyWorkingSetAtomicAsync([upgraded], (_, _, _, _) =>
                throw new InvalidOperationException("full interpretation receipt must suppress callback"),
                null, stop.Token);
            Assert.True(replay.JournalReplayHit);
            Assert.Equal(evidence, await EvidenceAsync());

            // A direct caller may provide a new facet for an existing E without
            // repeating an entity or testimony row. It remains real admitted work.
            var facetOnly = upgraded with
            {
                Entities = [], Physicalities = [], Attestations = [],
                EntityInterpretations = [new EntityInterpretationRow(id, 9, H("third-type"), source)]
            };
            var facet = await writer.ApplyWorkingSetAsync([facetOnly], stop.Token);
            Assert.False(facet.JournalReplayHit);
            Assert.Equal(0, facet.EntitiesInserted);
            Assert.Equal(0, facet.AttestationsInserted);
            Assert.Equal(3, await FacetsAsync());
            Assert.Equal(evidence, await EvidenceAsync());
            var facetReplay = await writer.ApplyWorkingSetAsync([facetOnly], stop.Token);
            Assert.True(facetReplay.JournalReplayHit);

            var absent = facetOnly with
            {
                EntityInterpretations = [new EntityInterpretationRow(H("absent"), 9, type, source)]
            };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                writer.ApplyWorkingSetAsync([absent], stop.Token));
            Assert.Contains("absent canonical entity", error.Message);
            Assert.Equal(3, await FacetsAsync());
        }
        finally
        {
            await using var cleanup = pg.DataSource.CreateCommand("""
                WITH owners AS (
                    DELETE FROM laplace.ingest_flush_journal_sources s
                    USING laplace.ingest_flush_journal j
                    WHERE s.working_set_id=j.working_set_id AND j.source_id=$1),
                receipts AS (
                    DELETE FROM laplace.ingest_flush_journal WHERE source_id=$1),
                witnesses AS (
                    DELETE FROM laplace.attestations WHERE source_id=$1),
                interpretations AS (
                    DELETE FROM laplace.entity_interpretations WHERE entity_id=ANY($2::bytea[]))
                DELETE FROM laplace.entities WHERE id=ANY($2::bytea[])
                """);
            cleanup.Parameters.AddWithValue(source.ToBytes());
            cleanup.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                new[] { id.ToBytes(), H("absent").ToBytes() });
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FacetSourceMinimumAvoidsRewritingUnchangedRows(bool ordinaryInsert)
    {
        string scope = "interpretation-source-min-" + Guid.NewGuid().ToString("N");
        Hash128 H(string value) => Hash128.OfCanonical(scope + "/" + value);
        var owner = H("owner");
        var id = H("entity");
        var type = H("type");
        var low = Hash128.FromBytes(Enumerable.Repeat((byte)0x11,16).ToArray());
        var high = Hash128.FromBytes(Enumerable.Repeat((byte)0xee,16).ToArray());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        int turn = 0;
        async Task PublishAsync(Hash128? source)
        {
            if (ordinaryInsert)
            {
                await using var command = pg.DataSource.CreateCommand("""
                    INSERT INTO laplace.entities(id,tier,type_id,first_observed_by)
                    VALUES($1,3,$2,$3)
                    ON CONFLICT(id) DO NOTHING
                    """);
                command.Parameters.AddWithValue(id.ToBytes());
                command.Parameters.AddWithValue(type.ToBytes());
                command.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Bytea,
                    Value = source.HasValue ? source.Value.ToBytes() : DBNull.Value
                });
                await command.ExecuteNonQueryAsync(stop.Token);
            }
            else
            {
                using var builder = new SubstrateChangeBuilder(owner, scope + "/" + turn++);
                // Each independently committed source unit reaches the real SQL
                // publication owner; full receipt replay cannot hide a no-op UPDATE.
                builder.AddEntity(id,3,type,source);
                var writer = new NpgsqlSubstrateWriter(pg.DataSource,
                    durability: PostgresWriteDurability.Synchronous);
                var applied = await writer.ApplyWorkingSetAsync([builder.Build()],stop.Token);
                Assert.False(applied.JournalReplayHit);
            }
        }
        async Task<(string? Source,string FacetXmin,string FacetCtid,
                    string EntityXmin,string EntityCtid)> SnapshotAsync()
        {
            await using var command = pg.DataSource.CreateCommand("""
                SELECT encode(i.first_observed_by,'hex'),i.xmin::text,i.ctid::text,
                       e.xmin::text,e.ctid::text,
                       i.first_observed_by IS NOT DISTINCT FROM e.first_observed_by
                FROM laplace.entity_interpretations i
                JOIN laplace.entities e ON e.id=i.entity_id
                WHERE i.entity_id=$1 AND i.tier=3 AND i.type_id=$2
                """);
            command.Parameters.AddWithValue(id.ToBytes());
            command.Parameters.AddWithValue(type.ToBytes());
            await using var row = await command.ExecuteReaderAsync(stop.Token);
            Assert.True(await row.ReadAsync(stop.Token));
            Assert.True(row.GetBoolean(5));
            var result = (row.IsDBNull(0) ? null : row.GetString(0),
                row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4));
            Assert.False(await row.ReadAsync(stop.Token));
            return result;
        }
        try
        {
            await PublishAsync(null);
            var empty = await SnapshotAsync();
            Assert.Null(empty.Source);
            await PublishAsync(null);
            Assert.Equal(empty,await SnapshotAsync());
            await PublishAsync(high);
            var populated = await SnapshotAsync();
            Assert.Equal(Convert.ToHexString(high.ToBytes()).ToLowerInvariant(),populated.Source);
            Assert.NotEqual(empty.FacetXmin,populated.FacetXmin);
            await PublishAsync(high);
            Assert.Equal(populated,await SnapshotAsync());
            await PublishAsync(null);
            Assert.Equal(populated,await SnapshotAsync());
            await PublishAsync(low);
            var minimum = await SnapshotAsync();
            Assert.Equal(Convert.ToHexString(low.ToBytes()).ToLowerInvariant(),minimum.Source);
            Assert.NotEqual(populated.FacetXmin,minimum.FacetXmin);
            await PublishAsync(high);
            Assert.Equal(minimum,await SnapshotAsync());
            await PublishAsync(low);
            Assert.Equal(minimum,await SnapshotAsync());
        }
        finally
        {
            await using var cleanup = pg.DataSource.CreateCommand("""
                WITH owners AS (
                    DELETE FROM laplace.ingest_flush_journal_sources s
                    USING laplace.ingest_flush_journal j
                    WHERE s.working_set_id=j.working_set_id AND j.source_id=$1),
                receipts AS (
                    DELETE FROM laplace.ingest_flush_journal WHERE source_id=$1),
                facets AS (
                    DELETE FROM laplace.entity_interpretations WHERE entity_id=$2)
                DELETE FROM laplace.entities WHERE id=$2
                """);
            cleanup.Parameters.AddWithValue(owner.ToBytes());
            cleanup.Parameters.AddWithValue(id.ToBytes());
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task NativeFacetOnlyUpgradeCannotHideBehindHistoricalSemanticReceipt()
    {
        string scope = "native-facet-upgrade-" + Guid.NewGuid().ToString("N");
        Hash128 H(string value) => Hash128.OfCanonical(scope + "/" + value);
        var source = H("source");
        var id = H("content");
        var type = H("type");
        var relation = H("relation");
        var witness = NativeAttestation.CategoricalResolved(id,relation,null,source,null,1.0);
        using var stage = IntentStage.New(1);
        stage.AddEntity(id,3,type,source);
        stage.AddAttestation(witness.Id,id,relation,null,source,null,
            outcome:2,lastObservedAtUnixUs:0,observationCount:1,
            sumScoreFp1e9:1_000_000_000L,opponentRdFp1e9:30_000_000_000L);
        using var builder = new SubstrateChangeBuilder(source,scope);
        var change = builder.Build() with { IntentStages = [stage] };
        var digest = stage.SemanticDigest();
        int callbacks = 0, acceptedSemantics = 0;
        Task Participant(NpgsqlConnection connection,NpgsqlTransaction transaction,
            WorkingSetAcceptedEvidence accepted,CancellationToken ct)
        {
            callbacks++;
            if (!accepted.OriginalReplay) acceptedSemantics++;
            return Task.CompletedTask;
        }
        async Task<(long Facets,string Evidence)> StateAsync()
        {
            await using var command = pg.DataSource.CreateCommand("""
                SELECT (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id=$1),
                       (SELECT COALESCE(jsonb_agg(to_jsonb(a) ORDER BY a.id)::text,'[]')
                        FROM laplace.attestations a WHERE source_id=$2)
                """);
            command.Parameters.AddWithValue(id.ToBytes());
            command.Parameters.AddWithValue(source.ToBytes());
            await using var row = await command.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            return (row.GetInt64(0),row.GetString(1));
        }
        try
        {
            var writer = new NpgsqlSubstrateWriter(pg.DataSource,
                durability:PostgresWriteDurability.Synchronous);
            var first = await writer.ApplyWorkingSetAtomicAsync([change],Participant,null);
            Assert.Equal(1,first.EntitiesInserted);
            Assert.Equal(1,first.AttestationsInserted);
            var baseline = await StateAsync();
            Assert.Equal(1,baseline.Facets);
            Assert.NotEqual("[]",baseline.Evidence);
            stage.AddEntityInterpretation(id,7,H("extra-type"),source);
            Assert.Equal(digest,stage.SemanticDigest());
            var upgraded = await writer.ApplyWorkingSetAtomicAsync([change],Participant,null);
            Assert.False(upgraded.JournalReplayHit);
            Assert.Equal(0,upgraded.EntitiesInserted);
            Assert.Equal(0,upgraded.AttestationsInserted);
            Assert.Equal(2,callbacks);
            Assert.Equal(1,acceptedSemantics);
            Assert.Equal((2L,baseline.Evidence),await StateAsync());
            var replay = await writer.ApplyWorkingSetAtomicAsync([change],(_,_,_,_) =>
                throw new InvalidOperationException("full auxiliary replay invoked callback"),null);
            Assert.True(replay.JournalReplayHit);

            using var facetStage = IntentStage.New(0);
            facetStage.AddEntityInterpretation(id,9,H("third-type"),source);
            using var facetBuilder = new SubstrateChangeBuilder(source,scope+"/facet-only");
            var only = facetBuilder.Build() with { IntentStages = [facetStage] };
            Assert.Equal(0,facetStage.EntityCount);
            Assert.Equal(0,facetStage.PhysicalityCount);
            Assert.Equal(0,facetStage.AttestationCount);
            var applied = await writer.ApplyWorkingSetAsync([only]);
            Assert.False(applied.JournalReplayHit);
            Assert.Equal(0,applied.EntitiesInserted);
            Assert.Equal(0,applied.AttestationsInserted);
            Assert.Equal((3L,baseline.Evidence),await StateAsync());
            Assert.True((await writer.ApplyWorkingSetAsync([only])).JournalReplayHit);
        }
        finally
        {
            await using var cleanup = pg.DataSource.CreateCommand("""
                WITH owners AS (
                    DELETE FROM laplace.ingest_flush_journal_sources s
                    USING laplace.ingest_flush_journal j
                    WHERE s.working_set_id=j.working_set_id AND j.source_id=$1),
                receipts AS (
                    DELETE FROM laplace.ingest_flush_journal WHERE source_id=$1),
                evidence AS (
                    DELETE FROM laplace.attestations WHERE source_id=$1),
                facets AS (
                    DELETE FROM laplace.entity_interpretations WHERE entity_id=$2)
                DELETE FROM laplace.entities WHERE id=$2
                """);
            cleanup.Parameters.AddWithValue(source.ToBytes());
            cleanup.Parameters.AddWithValue(id.ToBytes());
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CompleteNativeMetadataOwnsInsertPairAndMissingCoverageWritesNothing()
    {
        string scope = "native-facet-coverage-" + Guid.NewGuid().ToString("N");
        Hash128 H(string value) => Hash128.OfCanonical(scope + "/" + value);
        var source = H("source");
        var id = H("entity");
        var oldType = H("old-compatibility-type");
        var actualType = H("actual-observed-type");
        var relation = H("relation");
        var witness = NativeAttestation.CategoricalResolved(id,relation,null,source,null,1.0);
        using var stage = IntentStage.New(1);
        stage.AddEntity(id,3,oldType,source);
        stage.AddAttestation(witness.Id,id,relation,null,source,null,
            outcome:2,lastObservedAtUnixUs:0,observationCount:1,
            sumScoreFp1e9:1_000_000_000L,opponentRdFp1e9:30_000_000_000L);
        var originalBytes = stage.EmitCopyBinary(IntentStageTable.Entities);
        var originalDigest = stage.SemanticDigest();
        stage.ImportEntityInterpretations([]);
        using var builder = new SubstrateChangeBuilder(source,scope);
        var change = builder.Build() with { IntentStages = [stage] };
        var writer = new NpgsqlSubstrateWriter(pg.DataSource,
            durability:PostgresWriteDurability.Synchronous);
        try
        {
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                writer.ApplyWorkingSetAsync([change]));
            Assert.Contains("omits a staged canonical entity",rejected.Message);
            await using (var absent = pg.DataSource.CreateCommand("""
                SELECT (SELECT count(*) FROM laplace.entities WHERE id=$1),
                       (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id=$1),
                       (SELECT count(*) FROM laplace.attestations WHERE source_id=$2),
                       (SELECT count(*) FROM laplace.ingest_flush_journal WHERE source_id=$2)
                """))
            {
                absent.Parameters.AddWithValue(id.ToBytes());
                absent.Parameters.AddWithValue(source.ToBytes());
                await using var row = await absent.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync());
                for (int field=0;field<4;field++) Assert.Equal(0L,row.GetInt64(field));
            }
            using var actual = IntentStage.New(0);
            actual.AddEntityInterpretation(id,7,actualType,source);
            stage.ImportEntityInterpretations(actual.EmitEntityInterpretationTuples());
            var accepted = await writer.ApplyWorkingSetAsync([change]);
            Assert.Equal(1,accepted.EntitiesInserted);
            Assert.Equal(1,accepted.AttestationsInserted);
            Assert.Equal(originalBytes,stage.EmitCopyBinary(IntentStageTable.Entities));
            Assert.Equal(originalDigest,stage.SemanticDigest());
            await using var state = pg.DataSource.CreateCommand("""
                SELECT e.tier,e.type_id,
                       (SELECT count(*) FROM laplace.entity_interpretations WHERE entity_id=$1),
                       EXISTS(SELECT FROM laplace.entity_interpretations
                              WHERE entity_id=$1 AND tier=7 AND type_id=$2)
                FROM laplace.entities e WHERE id=$1
                """);
            state.Parameters.AddWithValue(id.ToBytes());
            state.Parameters.AddWithValue(actualType.ToBytes());
            await using var result = await state.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            Assert.Equal(7,result.GetInt16(0));
            Assert.Equal(actualType.ToBytes(),result.GetFieldValue<byte[]>(1));
            Assert.Equal(1L,result.GetInt64(2));
            Assert.True(result.GetBoolean(3));
        }
        finally
        {
            await using var cleanup = pg.DataSource.CreateCommand("""
                WITH owners AS (
                    DELETE FROM laplace.ingest_flush_journal_sources s
                    USING laplace.ingest_flush_journal j
                    WHERE s.working_set_id=j.working_set_id AND j.source_id=$1),
                receipts AS (
                    DELETE FROM laplace.ingest_flush_journal WHERE source_id=$1),
                evidence AS (
                    DELETE FROM laplace.attestations WHERE source_id=$1),
                facets AS (
                    DELETE FROM laplace.entity_interpretations WHERE entity_id=$2)
                DELETE FROM laplace.entities WHERE id=$2
                """);
            cleanup.Parameters.AddWithValue(source.ToBytes());
            cleanup.Parameters.AddWithValue(id.ToBytes());
            await cleanup.ExecuteNonQueryAsync();
        }
    }

}
