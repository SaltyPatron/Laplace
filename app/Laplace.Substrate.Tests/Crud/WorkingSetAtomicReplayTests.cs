using global::Npgsql;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

/// <summary>
/// Fault-injection coverage for the working-set acceptance boundary. The failure
/// is raised after a novel attestation COPY has started and while a present
/// attestation would be additively merged.
/// </summary>
[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class WorkingSetAtomicReplayTests
{
    private readonly LocalPgFixture _pg;

    public WorkingSetAtomicReplayTests(LocalPgFixture pg) => _pg = pg;

    private static Hash128 H(string seed) => Hash128.OfCanonical($"ws-atomic-replay/{seed}");

    private static AttestationRow Att(string seed, long games, long unixUs) => new(
        H($"att/{seed}"), H($"subject/{seed}"), H("relation"), null, H("source"), null,
        AttestationOutcome.Confirm, unixUs, games,
        1_000_000_000L, 30_000_000_000L);

    private async Task<long> ObservationCountAsync(Hash128 id)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT observation_count FROM laplace.attestations WHERE id = $1");
        cmd.Parameters.AddWithValue(id.ToBytes());
        object? value = await cmd.ExecuteScalarAsync();
        return value is null ? 0 : (long)value;
    }

    private async Task<long> JournalCountAsync(Hash128 sourceId)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.ingest_flush_journal WHERE source_id = $1");
        cmd.Parameters.AddWithValue(sourceId.ToBytes());
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task WorkingSetReplay_PostCopyMergeFailureRollsBackEvidenceAndJournal()
    {
        var writer = new NpgsqlSubstrateWriter(_pg.DataSource);
        var source = H("source");
        const int novelRows = 128; // finalized COPY payload exceeds one 8 KiB transport unit

        var existing = Att("existing", 2, IntentStage.PgEpochUnixUs);
        await writer.ApplyAsync(new SubstrateChangeBuilder(source, "seed-existing")
            .AddAttestation(existing)
            .Build());

        var staged = new SubstrateChangeBuilder(source, "atomic-retry", attestationCapacity: novelRows + 1)
            .AddAttestation(existing with
            {
                ObservationCount = 3,
                LastObservedAtUnixUs = IntentStage.PgEpochUnixUs + 1_000_000,
            });
        for (int i = 0; i < novelRows; i++)
            staged.AddAttestation(Att($"novel/{i}", 5, IntentStage.PgEpochUnixUs + 2_000_000));
        var change = staged.Build();

        await InstallMergeFailureTriggerAsync(existing.Id);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(
                () => writer.ApplyWorkingSetAsync(change));

            // The failing merge is after evidence COPY. A failed acceptance may
            // leave identity-only entity/physicality rows from detached COPY,
            // but it must leave neither additive testimony nor its replay claim.
            Assert.Equal(2, await ObservationCountAsync(existing.Id));
            for (int i = 0; i < novelRows; i++)
                Assert.Equal(0, await ObservationCountAsync(H($"att/novel/{i}")));
            Assert.Equal(0, await JournalCountAsync(source));
        }
        finally
        {
            await RemoveMergeFailureTriggerAsync();
        }

        var retried = await writer.ApplyWorkingSetAsync(change);
        Assert.False(retried.JournalReplayHit);
        Assert.Equal(5, await ObservationCountAsync(existing.Id));
        for (int i = 0; i < novelRows; i++)
            Assert.Equal(5, await ObservationCountAsync(H($"att/novel/{i}")));
        Assert.Equal(1, await JournalCountAsync(source));

        var replay = await writer.ApplyWorkingSetAsync(change);
        Assert.True(replay.JournalReplayHit);
        Assert.Equal(5, await ObservationCountAsync(existing.Id));
        for (int i = 0; i < novelRows; i++)
            Assert.Equal(5, await ObservationCountAsync(H($"att/novel/{i}")));
    }

    [Fact]
    public async Task ConsensusWorkingSet_FoldFailureRollsBackEvidenceJournalAndConsensus()
    {
        var source = H("consensus/source");
        var attestation = new AttestationRow(
            H("consensus/att"), H("consensus/subject"), H("consensus/relation"), null,
            source, null, AttestationOutcome.Confirm, IntentStage.PgEpochUnixUs, 5,
            1_000_000_000L, 30_000_000_000L);
        var change = new SubstrateChangeBuilder(source, "atomic-consensus")
            .AddAttestation(attestation)
            .Build();

        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await InstallConsensusFailureTriggerAsync();
        try
        {
            await Assert.ThrowsAsync<PostgresException>(
                () => writer.ApplyWorkingSetAsync(change));
            Assert.Equal(0, await ObservationCountAsync(attestation.Id));
            Assert.Equal(0, await JournalCountAsync(source));
            Assert.Equal(0, await ConsensusWitnessCountAsync(attestation.SubjectId));
        }
        finally
        {
            await RemoveConsensusFailureTriggerAsync();
        }

        var retry = await writer.ApplyWorkingSetAsync(change);
        Assert.False(retry.JournalReplayHit);
        Assert.Equal(5, await ObservationCountAsync(attestation.Id));
        Assert.Equal(1, await JournalCountAsync(source));
        Assert.Equal(5, await ConsensusWitnessCountAsync(attestation.SubjectId));

        var replay = await writer.ApplyWorkingSetAsync(change);
        Assert.True(replay.JournalReplayHit);
        Assert.Equal(5, await ObservationCountAsync(attestation.Id));
        Assert.Equal(5, await ConsensusWitnessCountAsync(attestation.SubjectId));
    }

    private async Task<long> ConsensusWitnessCountAsync(Hash128 subjectId)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT coalesce(sum(witness_count), 0) FROM laplace.consensus WHERE subject_id = $1");
        cmd.Parameters.AddWithValue(subjectId.ToBytes());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task InstallMergeFailureTriggerAsync(Hash128 id)
    {
        string hex = Convert.ToHexString(id.ToBytes()).ToLowerInvariant();
        await using var cmd = _pg.DataSource.CreateCommand($"""
            CREATE OR REPLACE FUNCTION public.laplace_test_atomic_merge_failure()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.id = decode('{hex}', 'hex') THEN
                    RAISE EXCEPTION 'injected post-copy attestation merge failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER laplace_test_atomic_merge_failure
            BEFORE UPDATE ON laplace.attestations
            FOR EACH ROW EXECUTE FUNCTION public.laplace_test_atomic_merge_failure();
            ALTER TABLE laplace.attestations
                ENABLE ALWAYS TRIGGER laplace_test_atomic_merge_failure;
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RemoveMergeFailureTriggerAsync()
    {
        await using var cmd = _pg.DataSource.CreateCommand("""
            DROP TRIGGER IF EXISTS laplace_test_atomic_merge_failure ON laplace.attestations;
            DROP FUNCTION IF EXISTS public.laplace_test_atomic_merge_failure();
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InstallConsensusFailureTriggerAsync()
    {
        await using var cmd = _pg.DataSource.CreateCommand("""
            CREATE OR REPLACE FUNCTION public.laplace_test_atomic_consensus_failure()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'injected consensus acceptance failure';
            END $$;
            CREATE TRIGGER laplace_test_atomic_consensus_failure
            BEFORE INSERT OR UPDATE ON laplace.consensus
            FOR EACH ROW EXECUTE FUNCTION public.laplace_test_atomic_consensus_failure();
            ALTER TABLE laplace.consensus
                ENABLE ALWAYS TRIGGER laplace_test_atomic_consensus_failure;
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RemoveConsensusFailureTriggerAsync()
    {
        await using var cmd = _pg.DataSource.CreateCommand("""
            DROP TRIGGER IF EXISTS laplace_test_atomic_consensus_failure ON laplace.consensus;
            DROP FUNCTION IF EXISTS public.laplace_test_atomic_consensus_failure();
            """);
        await cmd.ExecuteNonQueryAsync();
    }
}
