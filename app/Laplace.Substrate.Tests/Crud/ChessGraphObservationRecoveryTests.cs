using Laplace.Chess.Service;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD.Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class ChessGraphObservationRecoveryTests(LocalPgFixture pg)
{
    [Fact]
    public async Task ExistingPositionEntityRestoresMissingChildAndFormsThenReplaysWithoutWrites()
    {
        string scope = Guid.NewGuid().ToString("N");
        var source = SubstrateCanonicalIds.Source($"ChessGraphRecovery-{scope}");
        // A declared rules operand gives this fixture its own canonical position
        // and rule atom in the shared test database. No gameplay is claimed here.
        int checks = 1024 + (int)(BitConverter.ToUInt32(Guid.NewGuid().ToByteArray()) % 1_000_000);
        var rules = ChessVariantRules.Standard with { WinByCheckCount = checks };
        var composed = ChessCompose.Position(Board.FromFen(ChessModality.StartFen), rules);
        var rule = composed.Substructures[0];
        var ordinary = new NpgsqlSubstrateWriter(pg.DataSource,
            durability: PostgresWriteDurability.Synchronous);
        using (var partial = new SubstrateChangeBuilder(source, "chess/partial-position"))
        {
            await ordinary.ApplyAsync(partial
                .AddEntity(source, EntityTier.Word, BootstrapIntentBuilder.SourceTypeId)
                .AddEntity(composed.Position.Id, composed.Position.Tier,
                    ChessVocabulary.PositionType, source).Build());
        }

        var reader = new NpgsqlSubstrateReader(pg.DataSource);
        var presenceScope = reader.CapturePresenceScope();
        Assert.True(BitmapBits.IsSet(
            await reader.EntitiesExistBitmapAsync([composed.Position.Id]), 0));
        reader.MarkProven([composed.Position.Id], presenceScope);
        Assert.True(reader.IsProvenPresent(composed.Position.Id));
        Assert.False(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([rule.Id]), 0));
        await using (var absent = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.physicalities WHERE id=$1"))
        {
            absent.Parameters.AddWithValue(composed.Position.PhysId.ToBytes());
            Assert.Equal(0L, (long)(await absent.ExecuteScalarAsync())!);
        }

        SubstrateChange Compose()
        {
            using var builder = new SubstrateChangeBuilder(source, "chess/same-position-observation")
                .DeclareSourcePrior(SourceTrust.StructuredCorpus).SetPresenceOracle(reader);
            ChessGraph.EmitComposed(builder, composed, source);
            return builder.Build();
        }

        await using var writer = new ConsensusAccumulatingWriter(ordinary, pg.DataSource);
        var repair = Compose();
        Assert.Equal(composed.Substructures.Count + 1, repair.PhysicalityObservations.Length);
        var first = await writer.ApplyWorkingSetAsync([repair]);
        await PhysicalityWriterTestSupport.AssertSelectedRowsAsync(pg.DataSource,
            repair.Entities.Select(row => row.Id), repair.Physicalities.Select(row => row.Id), []);

        var admission = Assert.IsType<PhysicalityAdmissionReceipt>(first.PhysicalityAdmission);
        Assert.Equal(repair.PhysicalityObservations.Length, admission.Forms.Length);
        foreach (var entity in new[] { rule.Id, composed.Position.Id })
        {
            int index = Enumerable.Range(0, repair.PhysicalityObservations.Length)
                .Single(i => repair.PhysicalityObservations[i].EntityId == entity);
            await PhysicalityWriterTestSupport.AssertDescriptorReadbackAsync(pg.DataSource,
                admission.Forms[index].DescriptorId, repair.PhysicalityObservations[index]);
        }

        string accepted = await SnapshotAsync(source);
        Assert.NotEqual("[]", accepted);
        var replay = Compose();
        Assert.Equal(repair.Metadata.IntentId, replay.Metadata.IntentId);
        var repeated = await writer.ApplyWorkingSetAsync([replay]);
        Assert.Equal(0, repeated.EntitiesInserted);
        Assert.Equal(0, repeated.PhysicalitiesInserted);
        Assert.Equal(0, repeated.AttestationsInserted);
        Assert.Equal(accepted, await SnapshotAsync(source));
        Assert.Equal(composed.Position.Id,
            ChessCompose.Position(Board.FromFen(ChessModality.StartFen), rules).Position.Id);
        await using var rootCount = pg.DataSource.CreateCommand(
            "SELECT count(*) FROM laplace.entities WHERE id=$1");
        rootCount.Parameters.AddWithValue(composed.Position.Id.ToBytes());
        Assert.Equal(1L, (long)(await rootCount.ExecuteScalarAsync())!);
    }

    private async Task<string> SnapshotAsync(Hash128 source)
    {
        await using var command = pg.DataSource.CreateCommand("""
            SELECT COALESCE(jsonb_agg(
                to_jsonb(a) || jsonb_build_object('standing',to_jsonb(c)) ORDER BY a.id),
                '[]'::jsonb)::text
            FROM laplace.attestations a
            JOIN laplace.consensus c ON c.type_id=a.type_id AND c.subject_id=a.subject_id
                AND c.object_id IS NOT DISTINCT FROM a.object_id
            WHERE a.source_id=$1 AND a.type_id=$2
            """);
        command.Parameters.AddWithValue(source.ToBytes());
        command.Parameters.AddWithValue(RelationTypeRegistry.Resolve("HAS_PHYSICALITY").Id.ToBytes());
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
