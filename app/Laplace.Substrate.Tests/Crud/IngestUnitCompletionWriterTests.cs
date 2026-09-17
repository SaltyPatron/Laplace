using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class IngestUnitCompletionWriterTests(LocalPgFixture pg)
{
    private sealed record Unit(Hash128 TrunkRootId, Hash128 Owner, int Layer)
        : ITrunkRootRecord, IIngestCompletionRecord
    {
        public Hash128 CompletionAttestationTypeId => IngestUnitCompletion.RelationTypeId(Layer);
        public Hash128 CompletionAttestationId =>
            IngestUnitCompletion.AttestationId(TrunkRootId, Owner, Layer);
    }

    private sealed class Handler : IIngestRecordHandler<Unit>
    {
        public IIngestDeferredUnit CreateDeferredUnit(Unit record) =>
            throw new InvalidOperationException("the completion gate must not compose");
        public void WalkWitness(Unit record, Hash128 root, SubstrateChangeBuilder builder, IIngestDeferredUnit unit) =>
            throw new InvalidOperationException("accepted completion must not re-observe the unit");
        public long UnitsPerRecord(Unit record) => 1;
    }

    private sealed class OwnedChanges : IDisposable
    {
        private readonly List<SubstrateChange> _changes = [];
        public SubstrateChange Keep(SubstrateChange change)
        {
            _changes.Add(change);
            return change;
        }
        public void Dispose()
        {
            foreach (var change in _changes)
                foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }

    [Fact]
    public async Task EntityOnlyUnitsRecoverAndReceiptsRemainOperationalAcrossReplayRefoldAndEviction()
    {
        using var owned = new OwnedChanges();
        CodepointPerfcache.LoadDefault();
        string sourceName = "UnitReceipt-" + Guid.NewGuid().ToString("N");
        var source = SubstrateCanonicalIds.Source(sourceName);
        var peer = SubstrateCanonicalIds.Source(sourceName + "-peer");
        var type = EntityTypeRegistry.SourceReference;
        var units = new[] { 0, 21, 255 }.Select(layer => new Unit(
            Hash128.OfCanonical(sourceName + "/unit/" + layer), source, layer)).ToArray();
        var relation = RelationTypeRegistry.RelationTypeId("IS_A");
        var subject = Hash128.OfCanonical(sourceName + "/subject");
        var obj = Hash128.OfCanonical(sourceName + "/object");
        var ordinary = new NpgsqlSubstrateWriter(pg.DataSource,
            durability: PostgresWriteDurability.Synchronous);
        using var carriers = new SubstrateChangeBuilder(source, "partial-unit-carriers")
            .AddEntity(source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId)
            .AddEntity(peer, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId);
        foreach (var unit in units)
            carriers.AddEntity(unit.TrunkRootId, EntityTier.Document, type, source);
        await ordinary.ApplyAsync(carriers.Build());

        var reader = new NpgsqlSubstrateReader(pg.DataSource);
        async Task AssertGateAsync(bool complete)
        {
            var remaining = units.ToList();
            using var builder = new SubstrateChangeBuilder(source, "gate-only");
            var skipped = await IngestExistenceGate.RemovePresentAsync(
                remaining, new Handler(), reader, builder, CancellationToken.None);
            Assert.Equal(complete ? units.Length : 0, skipped.Length);
            Assert.Equal(complete ? 0 : units.Length, remaining.Count);
            Assert.Empty(builder.Build().Attestations);
        }
        await AssertGateAsync(false);
        foreach (var unit in units)
            Assert.False(await reader.HasSourceCompletedAsync(source, unit.Layer));

        SubstrateChange Complete(string label, bool receipts = true)
        {
            using var b = new SubstrateChangeBuilder(source, label)
                .DeclareSourcePrior(SourceTrust.StructuredCorpus)
                .DeclareSourcePrior(peer, SourceTrust.StructuredCorpus)
                .AddEntity(subject, EntityTier.Word, type, source)
                .AddEntity(obj, EntityTier.Word, type, source)
                .AddAttestation(NativeAttestation.CategoricalResolved(
                    subject, relation, obj, source, units[1].TrunkRootId, 0.9));
            ContentEmitter.Emit(b, sourceName + " completed source body", source);
            if (receipts)
            {
                foreach (var unit in units)
                    IngestUnitCompletion.Emit(b, unit.TrunkRootId, source, unit.Layer);
                IngestUnitCompletion.Emit(b, units[1].TrunkRootId, peer, units[1].Layer);
            }
            return owned.Keep(b.Build());
        }

        await using var writer = new ConsensusAccumulatingWriter(ordinary, pg.DataSource);
        // A previously successful version has a real journal receipt and body
        // observations, but lacks the newly introduced unit-completion metadata.
        var oldOutput = Complete("accepted-unit-output", receipts: false);
        var completedOutput = Complete("accepted-unit-output");
        Assert.Equal(oldOutput.Metadata.IntentId, completedOutput.Metadata.IntentId);
        await writer.ApplyWorkingSetAsync(oldOutput);
        await AssertGateAsync(false);
        long foldedBeforeUpgrade = writer.CellsFolded;
        long observationsBeforeUpgrade = writer.ObservationsAccumulated;
        var upgrade = await writer.ApplyWorkingSetAsync(completedOutput);
        Assert.Equal(4L, upgrade.AttestationsInserted);
        Assert.Equal(foldedBeforeUpgrade, writer.CellsFolded);
        Assert.Equal(observationsBeforeUpgrade, writer.ObservationsAccumulated);
        await AssertGateAsync(true);
        foreach (var unit in units)
            Assert.False(await reader.HasSourceCompletedAsync(source, unit.Layer));
        Assert.True(foldedBeforeUpgrade > 1, "the real body must have generated physicality standing");

        async Task<string> SnapshotAsync()
        {
            await using var command = pg.DataSource.CreateCommand("""
                SELECT jsonb_build_object(
                    'evidence', COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.type_id,a.id)
                        FROM laplace.attestations a WHERE a.source_id=$1), '[]'::jsonb),
                    'standing', COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY c.type_id,c.id)
                        FROM laplace.consensus c WHERE c.subject_id=$2), '[]'::jsonb))::text
                """);
            command.Parameters.AddWithValue(source.ToBytes());
            command.Parameters.AddWithValue(subject.ToBytes());
            return (string)(await command.ExecuteScalarAsync())!;
        }
        async Task AssertNoOperationalStandingAsync()
        {
            await using var command = pg.DataSource.CreateCommand(
                "SELECT count(*) FROM laplace.consensus WHERE type_id=ANY($1::bytea[]) AND subject_id=ANY($2::bytea[])");
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                units.Select(unit => unit.CompletionAttestationTypeId.ToBytes()).ToArray());
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                units.Select(unit => unit.TrunkRootId.ToBytes()).ToArray());
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }
        await AssertNoOperationalStandingAsync();
        string accepted = await SnapshotAsync();
        var replay = await writer.ApplyWorkingSetAsync(Complete("accepted-unit-output"));
        Assert.Equal(0L, replay.AttestationsInserted);
        Assert.Equal(accepted, await SnapshotAsync());
        await using (var refold = pg.DataSource.CreateCommand("CALL ops.refold_source($1, false)"))
        {
            refold.Parameters.AddWithValue(sourceName);
            await refold.ExecuteNonQueryAsync();
        }
        await AssertNoOperationalStandingAsync();
        Assert.Equal(accepted, await SnapshotAsync());

        // Even an explicit rated-relation selection must invalidate owned unit
        // completion. Another actual owner's receipt for the same marker survives.
        await using (var evict = pg.DataSource.CreateCommand(
                         "CALL ops.evict_source(p_source => $1, p_relations => $2, p_drain => false)"))
        {
            evict.Parameters.AddWithValue(source.ToBytes());
            evict.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                new[] { relation.ToBytes() });
            await evict.ExecuteNonQueryAsync();
        }
        await AssertGateAsync(false);
        var peerReceipt = IngestUnitCompletion.AttestationId(units[1].TrunkRootId, peer, units[1].Layer);
        Assert.Contains(peerReceipt, await reader.PresentAttestationIdsAsync(
            units[1].CompletionAttestationTypeId, [peerReceipt]));
        var retained = await reader.EntitiesExistBitmapAsync(units.Select(unit => unit.TrunkRootId).ToArray());
        for (int index = 0; index < units.Length; index++)
            Assert.True((retained[index >> 3] & (1 << (index & 7))) != 0,
                "source eviction must retain every canonical unit entity");
    }

    [Fact]
    public async Task InterruptedEvictionInvalidatesAllCompletionGatesAndAdmitsExactRepair()
    {
        CodepointPerfcache.LoadDefault();
        string suffix = Guid.NewGuid().ToString("N");
        string sourceName = "InterruptedUnitReceipt-" + suffix;
        var source = SubstrateCanonicalIds.Source(sourceName);
        var peer = SubstrateCanonicalIds.Source(sourceName + "-peer");
        var file = Hash128.OfCanonical("interrupted-file/" + suffix);
        var unit = new Unit(Hash128.OfCanonical("interrupted-unit/" + suffix), source, 21);
        var relation = RelationTypeRegistry.RelationTypeId("IS_A");
        var secondRelation = RelationTypeRegistry.RelationTypeId("unit-eviction-second-" + suffix);
        var obj = Hash128.OfCanonical("interrupted-object/" + suffix);
        var firstEvidence = NativeAttestation.CategoricalResolved(
            unit.TrunkRootId, relation, obj, source, null, 0.9);
        var secondEvidence = NativeAttestation.CategoricalResolved(
            unit.TrunkRootId, secondRelation, obj, source, null, 0.9);
        var ordinary = new NpgsqlSubstrateWriter(pg.DataSource,
            durability: PostgresWriteDurability.Synchronous);
        await using var writer = new ConsensusAccumulatingWriter(ordinary, pg.DataSource);
        using var builder = new SubstrateChangeBuilder(source, "interrupted-eviction")
            .DeclareSourcePrior(SourceTrust.StructuredCorpus)
            .AddEntity(source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId)
            .AddEntity(peer, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId)
            .AddEntity(file, EntityTier.Document, EntityTypeRegistry.SourceReference, source)
            .AddEntity(unit.TrunkRootId, EntityTier.Document, EntityTypeRegistry.SourceReference, source)
            .AddEntity(obj, EntityTier.Word, EntityTypeRegistry.SourceReference, source)
            .AddEntity(secondRelation, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, source)
            .AddAttestation(firstEvidence)
            .AddAttestation(secondEvidence);
        IngestUnitCompletion.Emit(builder, unit.TrunkRootId, source, unit.Layer);
        IngestUnitCompletion.Emit(builder, unit.TrunkRootId, peer, unit.Layer);
        LayerCompletion.EmitFileMarker(builder, file, source, unit.Layer);
        var complete = builder.Build();
        await writer.ApplyWorkingSetAsync(complete);
        var reader = new NpgsqlSubstrateReader(pg.DataSource);
        Assert.Contains(unit.CompletionAttestationId, await reader.PresentAttestationIdsAsync(
            unit.CompletionAttestationTypeId, [unit.CompletionAttestationId]));
        Assert.True(await reader.HasFileCompletedAsync(file, source, unit.Layer));

        // The retained file journal is the existing source-to-file ownership edge
        // used by eviction, while the real writer above owns the v2 flush receipt.
        await using (var journal = pg.DataSource.CreateCommand("""
                         WITH run AS (
                             INSERT INTO laplace.ingest_run_journal
                                 (run_id, source_name, source_id, layer, status)
                             VALUES ($1, $2, $3, 21, 'ok')
                             RETURNING run_id)
                         INSERT INTO laplace.ingest_file_journal
                             (run_id, file_label, source_name, file_id, status)
                         SELECT run_id, 'interrupted.unit', $2, $4, 'ok' FROM run
                         """))
        {
            journal.Parameters.AddWithValue(Guid.NewGuid());
            journal.Parameters.AddWithValue(sourceName);
            journal.Parameters.AddWithValue(source.ToBytes());
            journal.Parameters.AddWithValue(file.ToBytes());
            await journal.ExecuteNonQueryAsync();
        }

        async Task<long> ReplayClaimsAsync()
        {
            await using var command = pg.DataSource.CreateCommand("""
                SELECT count(*) FROM laplace.ingest_flush_journal receipt
                WHERE receipt.source_id=$1 OR EXISTS (
                    SELECT 1 FROM laplace.ingest_flush_journal_sources owner
                    WHERE owner.working_set_id=receipt.working_set_id AND owner.source_id=$1)
                """);
            command.Parameters.AddWithValue(source.ToBytes());
            return (long)(await command.ExecuteScalarAsync())!;
        }
        async Task<string> EvidenceAsync()
        {
            await using var command = pg.DataSource.CreateCommand("""
                SELECT jsonb_agg(to_jsonb(a) ORDER BY a.type_id,a.id)::text
                FROM laplace.attestations a
                WHERE a.source_id=$1 AND a.type_id=ANY($2::bytea[])
                """);
            command.Parameters.AddWithValue(source.ToBytes());
            command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                new[] { relation.ToBytes(), secondRelation.ToBytes() });
            return (string)(await command.ExecuteScalarAsync())!;
        }
        Assert.True(await ReplayClaimsAsync() > 0);
        string acceptedEvidence = await EvidenceAsync();

        // The first relation is durably deleted. Failure in the second relation
        // leaves a real partial eviction; no completion gate may hide its repair.
        string guard = "unit_evict_guard_" + suffix;
        string sourceHex = Convert.ToHexString(source.ToBytes());
        string secondRelationHex = Convert.ToHexString(secondRelation.ToBytes());
        try
        {
            await using (var install = pg.DataSource.CreateCommand($"""
                CREATE FUNCTION laplace.{guard}() RETURNS trigger LANGUAGE plpgsql AS $guard$
                BEGIN
                    IF OLD.source_id = decode('{sourceHex}', 'hex')
                       AND OLD.type_id = decode('{secondRelationHex}', 'hex') THEN
                        RAISE EXCEPTION 'controlled unit eviction failure';
                    END IF;
                    RETURN OLD;
                END;
                $guard$;
                CREATE TRIGGER {guard} BEFORE DELETE ON laplace.attestations
                    FOR EACH ROW EXECUTE FUNCTION laplace.{guard}();
                """))
                await install.ExecuteNonQueryAsync();

            await using (var evict = pg.DataSource.CreateCommand(
                             "CALL ops.evict_source(p_source => $1, p_relations => $2, p_batch => 1, p_drain => false)"))
            {
                evict.Parameters.AddWithValue(source.ToBytes());
                evict.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                    new[] { relation.ToBytes(), secondRelation.ToBytes() });
                var failure = await Assert.ThrowsAsync<global::Npgsql.PostgresException>(
                    () => evict.ExecuteNonQueryAsync());
                Assert.Equal("P0001", failure.SqlState);
                Assert.Contains("controlled unit eviction failure", failure.MessageText);
            }

            Assert.Equal(0, await ReplayClaimsAsync());
            Assert.False(await reader.HasFileCompletedAsync(file, source, unit.Layer));
            Assert.Empty(await reader.PresentAttestationIdsAsync(
                unit.CompletionAttestationTypeId, [unit.CompletionAttestationId]));
            var peerReceipt = IngestUnitCompletion.AttestationId(unit.TrunkRootId, peer, unit.Layer);
            Assert.Contains(peerReceipt, await reader.PresentAttestationIdsAsync(
                unit.CompletionAttestationTypeId, [peerReceipt]));
            Assert.Empty(await reader.PresentAttestationIdsAsync(relation, [firstEvidence.Id]));
            Assert.Contains(secondEvidence.Id,
                await reader.PresentAttestationIdsAsync(secondRelation, [secondEvidence.Id]));
            var remaining = new List<Unit> { unit };
            using var gateBuilder = new SubstrateChangeBuilder(source, "interrupted-gate");
            Assert.Empty(await IngestExistenceGate.RemovePresentAsync(
                remaining, new Handler(), reader, gateBuilder, CancellationToken.None));
            Assert.Single(remaining);

            // This is the identical previously accepted payload, not a new label
            // that could evade a stale v2 replay claim. Existing evidence survives
            // unchanged; only the missing witness and completion rows are restored.
            var repaired = await writer.ApplyWorkingSetAsync(complete);
            Assert.False(repaired.JournalReplayHit);
            Assert.Equal(3L, repaired.AttestationsInserted);
            Assert.Equal(acceptedEvidence, await EvidenceAsync());
            Assert.True(await reader.HasFileCompletedAsync(file, source, unit.Layer));
            Assert.Contains(unit.CompletionAttestationId, await reader.PresentAttestationIdsAsync(
                unit.CompletionAttestationTypeId, [unit.CompletionAttestationId]));
            Assert.True(await ReplayClaimsAsync() > 0);
            var replay = await writer.ApplyWorkingSetAsync(complete);
            Assert.True(replay.JournalReplayHit);
            Assert.Equal(0L, replay.AttestationsInserted);
            Assert.Equal(acceptedEvidence, await EvidenceAsync());
        }
        finally
        {
            await using var remove = pg.DataSource.CreateCommand($"""
                DROP TRIGGER IF EXISTS {guard} ON laplace.attestations;
                DROP FUNCTION IF EXISTS laplace.{guard}();
                """);
            await remove.ExecuteNonQueryAsync();
        }
    }

}
