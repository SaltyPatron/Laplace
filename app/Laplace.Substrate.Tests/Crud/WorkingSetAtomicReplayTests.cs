using global::Npgsql;
using Laplace.Decomposers.Abstractions;
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

    private async Task<(long Owners, long WorkingSets)> JournalOwnersAsync(
        Hash128 first, Hash128 second)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT count(*), count(DISTINCT working_set_id) "
          + "FROM laplace.ingest_flush_journal_sources WHERE source_id = $1 OR source_id = $2");
        cmd.Parameters.AddWithValue(first.ToBytes());
        cmd.Parameters.AddWithValue(second.ToBytes());
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<(short Outcome, long Games, long Sum, bool FoldReplayable)?> EvidenceAsync(
        Hash128 id, Hash128 typeId, Hash128 subjectId)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT outcome, observation_count, sum_score_fp1e9, fold_replayable "
          + "FROM laplace.attestations WHERE id = $1 AND type_id = $2 AND subject_id = $3");
        cmd.Parameters.AddWithValue(id.ToBytes());
        cmd.Parameters.AddWithValue(typeId.ToBytes());
        cmd.Parameters.AddWithValue(subjectId.ToBytes());
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetInt16(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetBoolean(3));
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

    [Fact]
    public async Task TransientCategoricalFold_FailureRollsBack_ThenRetryConsumesMagnitudeOnce()
    {
        var source = H("transient/source");
        var relation = H("transient/relation");
        var obj = H("transient/object");
        var context = H("transient/circuit");
        var transientSubject = H("transient/subject");
        var ordinarySubject = H("ordinary/subject");

        // Persisted model evidence says only "confirm". The native calibration
        // used by the fold is deliberately refuting, proving that consensus did
        // not replay the receipt's categorical representative (1.0).
        var categorical = NativeAttestation.CategoricalResolvedOutcome(
            transientSubject, relation, obj, source, context,
            witnessWeight: 0.5, AttestationOutcome.Confirm);
        Assert.False(categorical.FoldReplayable);
        var ordinary = NativeAttestation.CategoricalResolved(
            ordinarySubject, relation, obj, source, context,
            witnessWeight: 0.5, confirm: true, observationCount: 2);
        var change = new SubstrateChangeBuilder(source, "atomic-transient")
            .AddAttestation(categorical)
            .AddEphemeralFold(new EphemeralFoldInput(
                categorical.Id, H("transient/calculation-receipt"), 100_000_000))
            .AddAttestation(ordinary)
            .Build();

        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        await InstallConsensusFailureTriggerAsync();
        try
        {
            await Assert.ThrowsAsync<PostgresException>(
                () => writer.ApplyWorkingSetAsync(change));
            Assert.Null(await EvidenceAsync(categorical.Id, relation, transientSubject));
            Assert.Null(await EvidenceAsync(ordinary.Id, relation, ordinarySubject));
            Assert.Equal(0, await JournalCountAsync(source));
            Assert.Equal(0, await ConsensusWitnessCountAsync(transientSubject));
            Assert.Equal(0, await ConsensusWitnessCountAsync(ordinarySubject));
        }
        finally
        {
            await RemoveConsensusFailureTriggerAsync();
        }

        var retry = await writer.ApplyWorkingSetAsync(change);
        Assert.False(retry.JournalReplayHit);
        var persisted = await EvidenceAsync(categorical.Id, relation, transientSubject);
        Assert.NotNull(persisted);
        Assert.Equal((short)AttestationOutcome.Confirm, persisted.Value.Outcome);
        Assert.Equal(1, persisted.Value.Games);
        Assert.Equal(1_000_000_000, persisted.Value.Sum);
        Assert.False(persisted.Value.FoldReplayable);

        var transientConsensus = await ConsensusRowAsync(transientSubject, relation, obj);
        var ordinaryConsensus = await ConsensusRowAsync(ordinarySubject, relation, obj);
        Assert.NotNull(transientConsensus);
        Assert.NotNull(ordinaryConsensus);
        Assert.True(transientConsensus.Value.Rating < Glicko2.DefaultRatingFp1e9);
        Assert.True(ordinaryConsensus.Value.Rating > Glicko2.DefaultRatingFp1e9);
        Assert.Equal(1, transientConsensus.Value.WitnessCount);
        Assert.Equal(2, ordinaryConsensus.Value.WitnessCount);

        var replay = await writer.ApplyWorkingSetAsync(change);
        Assert.True(replay.JournalReplayHit);
        Assert.Equal(transientConsensus, await ConsensusRowAsync(transientSubject, relation, obj));
        Assert.Equal(ordinaryConsensus, await ConsensusRowAsync(ordinarySubject, relation, obj));
        Assert.Equal(1, (await EvidenceAsync(categorical.Id, relation, transientSubject))!.Value.Games);
        Assert.Equal(2, (await EvidenceAsync(ordinary.Id, relation, ordinarySubject))!.Value.Games);
    }

    [Fact]
    public async Task MixedModelSources_CommitOneAtomicReceipt_RollbackRetryAndReplayOnce()
    {
        Hash128 sourceA = H("mixed/source/a");
        Hash128 sourceB = H("mixed/source/b");
        Hash128 relation = H("mixed/relation");
        Hash128 subject = H("mixed/subject");
        Hash128 obj = H("mixed/object");
        Hash128 orchestration = H("mixed/orchestration");
        var receiptA = NativeAttestation.CategoricalResolvedOutcome(
            subject, relation, obj, sourceA, H("mixed/context/a"),
            witnessWeight: 0.5, AttestationOutcome.Confirm);
        var receiptB = NativeAttestation.CategoricalResolvedOutcome(
            subject, relation, obj, sourceB, H("mixed/context/b"),
            witnessWeight: 0.5, AttestationOutcome.Refute);
        int lifetimeDisposals = 0;
        int verifications = 0;
        ValueTask VerifyBeforeCommit(CancellationToken _)
        {
            if (Volatile.Read(ref lifetimeDisposals) != 0)
                return ValueTask.FromException(new ObjectDisposedException("model snapshot"));
            if (Interlocked.Increment(ref verifications) == 1)
                return ValueTask.FromException(new InvalidDataException(
                    "injected source snapshot verification failure"));
            return ValueTask.CompletedTask;
        }
        using var envelopeOwner = SubstrateApplyEnvelope.Own(
            new DelegateDisposable(() => Interlocked.Increment(ref lifetimeDisposals)),
            VerifyBeforeCommit);
        SubstrateChange changeA = new SubstrateChangeBuilder(
                sourceA, $"model/corroboration/{orchestration}/a")
            .AddAttestation(receiptA)
            .AddEphemeralFold(new(receiptA.Id, H("mixed/calculation/a"), 900_000_000))
            .Build() with { ApplyEnvelope = envelopeOwner.Retain() };
        SubstrateChange changeB = new SubstrateChangeBuilder(
                sourceB, $"model/corroboration/{orchestration}/b")
            .AddAttestation(receiptB)
            .AddEphemeralFold(new(receiptB.Id, H("mixed/calculation/b"), 100_000_000))
            .Build() with { ApplyEnvelope = envelopeOwner.Retain() };
        envelopeOwner.Dispose();

        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        SubstrateChange[] forward = [changeB, changeA];
        SubstrateChange[] reverse = [changeA, changeB];
        Func<CancellationToken, ValueTask> verifier =
            SubstrateApplyEnvelope.ComposeVerifier(forward)!;

        // The verifier runs after the fold participant. Its failure must still
        // roll evidence, consensus, journal, and source ownership back together.
        await Assert.ThrowsAsync<InvalidDataException>(
            () => writer.ApplyWorkingSetAsync(
                forward, verifier));
        Assert.Null(await EvidenceAsync(receiptA.Id, relation, subject));
        Assert.Null(await EvidenceAsync(receiptB.Id, relation, subject));
        Assert.Equal((0L, 0L), await JournalOwnersAsync(sourceA, sourceB));
        Assert.Null(await ConsensusRowAsync(subject, relation, obj));

        ApplyResult retry = await writer.ApplyWorkingSetAsync(
            forward, verifier);
        Assert.False(retry.JournalReplayHit);
        var evidenceA = await EvidenceAsync(receiptA.Id, relation, subject);
        var evidenceB = await EvidenceAsync(receiptB.Id, relation, subject);
        Assert.NotNull(evidenceA);
        Assert.NotNull(evidenceB);
        // Durable evidence retains only the categorical representatives.  The
        // 0.9/0.1 continuous inputs were consumed by the atomic fold.
        Assert.Equal(1_000_000_000, evidenceA.Value.Sum);
        Assert.Equal(0, evidenceB.Value.Sum);
        Assert.False(evidenceA.Value.FoldReplayable);
        Assert.False(evidenceB.Value.FoldReplayable);
        Assert.Equal((2L, 1L), await JournalOwnersAsync(sourceA, sourceB));
        Assert.Equal(2, (await ConsensusRowAsync(subject, relation, obj))!.Value.WitnessCount);

        // Mixed-source ordering is transport only: the same complete analysis
        // replays even when the caller supplies its source changes in reverse.
        ApplyResult replay = await writer.ApplyWorkingSetAsync(
            reverse, SubstrateApplyEnvelope.ComposeVerifier(reverse)!);
        Assert.True(replay.JournalReplayHit);
        Assert.Equal(2, verifications);
        Assert.Equal(1, (await EvidenceAsync(receiptA.Id, relation, subject))!.Value.Games);
        Assert.Equal(1, (await EvidenceAsync(receiptB.Id, relation, subject))!.Value.Games);
        Assert.Equal(2, (await ConsensusRowAsync(subject, relation, obj))!.Value.WitnessCount);

        await using var sourceProof = _pg.DataSource.CreateCommand(
            "SELECT count(DISTINCT source_id) FROM laplace.attestations "
          + "WHERE subject_id = $1 AND type_id = $2 AND object_id = $3");
        sourceProof.Parameters.AddWithValue(subject.ToBytes());
        sourceProof.Parameters.AddWithValue(relation.ToBytes());
        sourceProof.Parameters.AddWithValue(obj.ToBytes());
        Assert.Equal(2, Convert.ToInt64(await sourceProof.ExecuteScalarAsync()));

        SubstrateApplyEnvelope.Release(forward);
        Assert.Equal(1, lifetimeDisposals);
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    [Fact]
    public async Task TransientCalculationReceipt_DistinguishesDirectWorkingSetRetryIdentity()
    {
        var source = H("receipt/source");
        var relation = H("receipt/relation");
        var subject = H("receipt/subject");
        var obj = H("receipt/object");
        var context = H("receipt/context");
        var receipt = NativeAttestation.CategoricalResolvedOutcome(
            subject, relation, obj, source, context,
            witnessWeight: 0.5, AttestationOutcome.Confirm);
        var first = new SubstrateChangeBuilder(source, "receipt/direct-identity")
            .AddAttestation(receipt)
            .AddEphemeralFold(new(receipt.Id, H("receipt/calculation/one"), 100_000_000))
            .Build();
        // Exercise the writer boundary rather than relying on the builder's
        // intent hash: callers can construct the public record directly.
        var second = first with
        {
            EphemeralFoldInputs =
                [new(receipt.Id, H("receipt/calculation/two"), 900_000_000)],
        };

        await using var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(_pg.DataSource), _pg.DataSource);
        Assert.False((await writer.ApplyWorkingSetAsync(first)).JournalReplayHit);
        Assert.False((await writer.ApplyWorkingSetAsync(second)).JournalReplayHit);

        var evidence = await EvidenceAsync(receipt.Id, relation, subject);
        Assert.NotNull(evidence);
        Assert.Equal(2, evidence.Value.Games);
        Assert.Equal(2_000_000_000, evidence.Value.Sum);
        Assert.False(evidence.Value.FoldReplayable);
        var consensus = await ConsensusRowAsync(subject, relation, obj);
        Assert.NotNull(consensus);
        Assert.Equal(2, consensus.Value.WitnessCount);

        Assert.True((await writer.ApplyWorkingSetAsync(second)).JournalReplayHit);
        Assert.Equal(consensus, await ConsensusRowAsync(subject, relation, obj));
        Assert.Equal(2, (await EvidenceAsync(receipt.Id, relation, subject))!.Value.Games);
    }

    private async Task<(long Rating, long WitnessCount)?> ConsensusRowAsync(
        Hash128 subjectId, Hash128 typeId, Hash128 objectId)
    {
        await using var cmd = _pg.DataSource.CreateCommand(
            "SELECT rating, witness_count FROM laplace.consensus "
          + "WHERE subject_id = $1 AND type_id = $2 AND object_id = $3");
        cmd.Parameters.AddWithValue(subjectId.ToBytes());
        cmd.Parameters.AddWithValue(typeId.ToBytes());
        cmd.Parameters.AddWithValue(objectId.ToBytes());
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetInt64(0), reader.GetInt64(1));
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
