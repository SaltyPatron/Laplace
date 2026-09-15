using global::Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class PostgresWriteDurabilityTests(LocalPgFixture pg)
{
    [Theory]
    [InlineData(PostgresWriteDurability.Asynchronous, "off")]
    [InlineData(PostgresWriteDurability.Synchronous, "on")]
    public async Task SelectedModeReachesActualCopyAndControlTransactions(
        PostgresWriteDurability mode, string expected)
    {
        string scope = Guid.NewGuid().ToString("N");
        Hash128 H(string part) => Hash128.OfCanonical("durability/" + scope + "/" + part);
        var source = H("source");
        var type = H("type");
        var first = H("entity/0");
        string sourceHex = Convert.ToHexStringLower(source.ToBytes());
        string typeHex = Convert.ToHexStringLower(type.ToBytes());
        string firstHex = Convert.ToHexStringLower(first.ToBytes());
        await using var setup = pg.DataSource.CreateCommand($"""
            CREATE TABLE laplace.test_copy_durability (transaction_id bigint, commit_mode text);
            CREATE FUNCTION laplace.test_copy_durability_capture() RETURNS trigger
            LANGUAGE plpgsql AS $$
            DECLARE selected boolean;
            BEGIN
              IF TG_ARGV[0] = 'entities' THEN
                selected := NEW.type_id = decode('{typeHex}','hex');
              ELSIF TG_ARGV[0] = 'physicalities' THEN
                selected := NEW.entity_id = decode('{firstHex}','hex');
              ELSE
                selected := NEW.source_id = decode('{sourceHex}','hex');
              END IF;
              IF selected THEN
                IF current_setting('synchronous_commit') <> '{expected}' THEN
                  RAISE EXCEPTION 'COPY did not use the selected commit acknowledgement';
                END IF;
                INSERT INTO laplace.test_copy_durability
                VALUES (txid_current(), current_setting('synchronous_commit'));
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER test_copy_durability BEFORE INSERT ON laplace.entities
              FOR EACH ROW EXECUTE FUNCTION laplace.test_copy_durability_capture('entities');
            ALTER TABLE laplace.entities ENABLE ALWAYS TRIGGER test_copy_durability;
            CREATE TRIGGER test_copy_durability BEFORE INSERT ON laplace.physicalities
              FOR EACH ROW EXECUTE FUNCTION laplace.test_copy_durability_capture('physicalities');
            ALTER TABLE laplace.physicalities ENABLE ALWAYS TRIGGER test_copy_durability;
            CREATE TRIGGER test_copy_durability BEFORE INSERT ON laplace.attestations
              FOR EACH ROW EXECUTE FUNCTION laplace.test_copy_durability_capture('attestations');
            ALTER TABLE laplace.attestations ENABLE ALWAYS TRIGGER test_copy_durability;
            """);
        await setup.ExecuteNonQueryAsync();
        try
        {
            var builder = new SubstrateChangeBuilder(source, "durability-copies");
            // Several transport pages exercise detached COPY when the actual host
            // has parallel apply capacity; one-core environments retain one transaction.
            for (int i = 0; i < 1024; i++) builder.AddEntity(H("entity/" + i), 3, type);
            builder.AddPhysicality(new PhysicalityRow(
                PhysicalityId.Compute(first, PhysicalityType.Content), first, source, PhysicalityType.Content,
                0.1, 0.2, 0.3, 0.4, Hilbert128.Encode([0.1, 0.2, 0.3, 0.4]), null, 0,
                null, null, IntentStage.PgEpochUnixUs));
            builder.AddAttestation(new AttestationRow(H("att"), first, H("relation"), null,
                source, null, AttestationOutcome.Confirm, IntentStage.PgEpochUnixUs, 1,
                1_000_000_000L, 30_000_000_000L));
            var writer = new NpgsqlSubstrateWriter(pg.DataSource, durability: mode);
            string? participantMode = null;
            var change = builder.Build();
            var applied = await writer.ApplyWorkingSetAtomicAsync([change], async (connection, transaction, _, ct) =>
            {
                var settings = await NpgsqlSubstrateWriter.ReadCommitSettingsAsync(connection, transaction, mode, ct);
                participantMode = settings.SynchronousCommit;
                Assert.False(settings.WriteCommitAcknowledged);
            }, reconciliation: null);
            Assert.Equal(expected, participantMode);
            var commit = Assert.IsType<PostgresCommitReceipt>(applied.PostgresCommit);
            Assert.Equal(expected, commit.SynchronousCommit);
            Assert.True(commit.WriteCommitAcknowledged);
            Assert.Equal(mode == PostgresWriteDurability.Synchronous, commit.LocalWalFlushAcknowledged);
            await using var observed = pg.DataSource.CreateCommand(
                "SELECT count(DISTINCT transaction_id), count(*) FROM laplace.test_copy_durability");
            await using (var rows = await observed.ExecuteReaderAsync())
            {
                Assert.True(await rows.ReadAsync());
                Assert.Equal(1026L, rows.GetInt64(1));
                Assert.Equal(rows.GetInt64(0), applied.CopyTransactionsStarted);
                Assert.Equal(applied.CopyTransactionsStarted, applied.CopyTransactionsCommitted);
            }
            var replay = await writer.ApplyWorkingSetAtomicAsync([change], (_, _, _, _) =>
                throw new InvalidOperationException("Replay must not invoke the transaction participant."), null);
            Assert.True(replay.JournalReplayHit);
            Assert.False(Assert.IsType<PostgresCommitReceipt>(replay.PostgresCommit).WriteCommitAcknowledged);
            Assert.Equal(0, replay.CopyTransactionsStarted);
            Assert.Equal(0, replay.CopyTransactionsCommitted);
        }
        finally
        {
            await using var cleanup = pg.DataSource.CreateCommand("""
                DROP TRIGGER IF EXISTS test_copy_durability ON laplace.entities;
                DROP TRIGGER IF EXISTS test_copy_durability ON laplace.physicalities;
                DROP TRIGGER IF EXISTS test_copy_durability ON laplace.attestations;
                DROP FUNCTION IF EXISTS laplace.test_copy_durability_capture();
                DROP TABLE IF EXISTS laplace.test_copy_durability;
                """);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task SynchronousParticipantFailureDoesNotAcknowledgeEvidenceOrJournal()
    {
        string scope = Guid.NewGuid().ToString("N");
        Hash128 H(string part) => Hash128.OfCanonical("durability-failure/" + scope + "/" + part);
        var source = H("source");
        var attestation = new AttestationRow(H("att"), H("subject"), H("relation"), null,
            source, null, AttestationOutcome.Confirm, IntentStage.PgEpochUnixUs, 1,
            1_000_000_000L, 30_000_000_000L);
        var change = new SubstrateChangeBuilder(source, "durability-failure").AddAttestation(attestation).Build();
        var writer = new NpgsqlSubstrateWriter(pg.DataSource, durability: PostgresWriteDurability.Synchronous);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ApplyWorkingSetAtomicAsync([change],
            (_, _, _, _) => throw new InvalidDataException("controlled participant failure"), null));
        await using var query = pg.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM laplace.attestations WHERE source_id=$1),
                   (SELECT count(*) FROM laplace.ingest_flush_journal WHERE source_id=$1)
            """);
        query.Parameters.AddWithValue(source.ToBytes());
        await using (var rows = await query.ExecuteReaderAsync())
        {
            Assert.True(await rows.ReadAsync());
            Assert.Equal(0L, rows.GetInt64(0));
            Assert.Equal(0L, rows.GetInt64(1));
        }
        var retried = await writer.ApplyWorkingSetAtomicAsync([change], (_, _, _, _) => Task.CompletedTask, null);
        Assert.True(Assert.IsType<PostgresCommitReceipt>(retried.PostgresCommit).LocalWalFlushAcknowledged);
    }
}
