using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD.Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class IngestUnitCompletionWriterTests(LocalPgFixture pg)
{
    private sealed record Unit(Hash128 TrunkRootId, Hash128 Owner, int Layer)
        : ITrunkRootRecord, IIngestCompletionRecord
    {
        public IngestUnitCompletionKey Key => IngestUnitCompletion.Key(TrunkRootId, Owner, Layer);
        public IngestUnitCompletionKey? Completion => Key;
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
    public async Task EntityOnlyUnitsRecoverAndCompletionsRemainOperationalAcrossReplayRefoldAndEviction()
    {
        using var owned = new OwnedChanges();
        CodepointPerfcache.LoadDefault();
        string sourceName = "UnitReceipt-" + Guid.NewGuid().ToString("N");
        var source = SubstrateCanonicalIds.Source(sourceName);
        var peer = SubstrateCanonicalIds.Source(sourceName + "-peer");
        var type = EntityTypeRegistry.SourceReference;
        var units = new[] { 0, 21, 255 }.Select(layer => new Unit(
            Hash128.Text(sourceName + "/unit/" + layer), source, layer)).ToArray();
        var relation = RelationTypeRegistry.RelationTypeId("IS_A");
        var subject = Hash128.Text(sourceName + "/subject");
        var obj = Hash128.Text(sourceName + "/object");
        var ordinary = new NpgsqlSubstrateWriter(pg.DataSource,
            durability: PostgresWriteDurability.Synchronous);
        using var carriers = new SubstrateChangeBuilder(source, "partial-unit-carriers")
            .AddEntity(source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId)
            .AddEntity(peer, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId);
        foreach (var unit in units)
            carriers.AddEntity(unit.TrunkRootId, EntityTier.Document, type);
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
                .AddEntity(subject, EntityTier.Word, type)
                .AddEntity(obj, EntityTier.Word, type)
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
        // observations, but lacks unit completion state. Completion never enters
        // the source-unit identity, so the completed payload replays the accepted
        // working set and records only the completion rows it owes.
        var oldOutput = Complete("accepted-unit-output", receipts: false);
        var completedOutput = Complete("accepted-unit-output");
        Assert.Equal(oldOutput.Metadata.IntentId, completedOutput.Metadata.IntentId);
        await writer.ApplyWorkingSetAsync(oldOutput);
        await AssertGateAsync(false);
        long foldedBeforeUpgrade = writer.CellsFolded;
        long observationsBeforeUpgrade = writer.ObservationsAccumulated;
        var upgrade = await writer.ApplyWorkingSetAsync(completedOutput);
        Assert.True(upgrade.JournalReplayHit);
        Assert.Equal(0L, upgrade.AttestationsInserted);
        Assert.Equal(foldedBeforeUpgrade, writer.CellsFolded);
        Assert.Equal(observationsBeforeUpgrade, writer.ObservationsAccumulated);
        await AssertGateAsync(true);
        foreach (var unit in units)
            Assert.False(await reader.HasSourceCompletedAsync(source, unit.Layer));
        Assert.Equal(1L, foldedBeforeUpgrade);

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
        // Completion is operational state: no unit is ever the subject of testimony
        // or standing because it completed.
        async Task AssertNoOperationalStandingAsync()
        {
            await using var command = pg.DataSource.CreateCommand("""
                SELECT (SELECT count(*) FROM laplace.attestations WHERE subject_id=ANY($1::bytea[]))
                     + (SELECT count(*) FROM laplace.consensus WHERE subject_id=ANY($1::bytea[]))
                """);
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
        // completion. Another witness's completion of the same unit survives.
        await using (var evict = pg.DataSource.CreateCommand(
                         "CALL ops.evict_source(p_source => $1, p_relations => $2, p_drain => false)"))
        {
            evict.Parameters.AddWithValue(source.ToBytes());
            evict.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea,
                new[] { relation.ToBytes() });
            await evict.ExecuteNonQueryAsync();
        }
        await AssertGateAsync(false);
        var peerReceipt = IngestUnitCompletion.Key(units[1].TrunkRootId, peer, units[1].Layer);
        Assert.Contains(peerReceipt, await reader.CompletedUnitsAsync([peerReceipt]));
        var retained = await reader.EntitiesExistBitmapAsync(units.Select(unit => unit.TrunkRootId).ToArray());
        for (int index = 0; index < units.Length; index++)
            Assert.True((retained[index >> 3] & (1 << (index & 7))) != 0,
                "source eviction must retain every canonical unit entity");
    }

}
