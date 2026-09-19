using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class ForwardRealizationDbTests(LocalPgFixture pg)
{
    [Fact]
    public async Task SemanticFrameSelectionProjectsToItsWitnessedTextSurface()
    {
        CodepointPerfcache.LoadDefault();

        string nonce = Guid.NewGuid().ToString("N");
        string unit = $"forward-realize/{nonce}";
        string surfaceText = $"forward-realize-surface-{nonce}";
        Hash128 source = Hash128.OfCanonical($"{unit}/source");
        Hash128 concept = Hash128.OfCanonical($"{unit}/frame");

        using var builder = new SubstrateChangeBuilder(source, unit)
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
        builder.AddEntity(
            source,
            EntityTier.Word,
            BootstrapIntentBuilder.SourceTypeId,
            source);
        builder.AddEntity(
            concept,
            EntityTier.Word,
            EntityTypeRegistry.Id("FrameNet_Frame"),
            source);

        Hash128 surface = ContentEmitter.Emit(builder, surfaceText, source)
            ?? throw new InvalidOperationException("test surface did not enter the content spine");
        builder.AddAttestation(NativeAttestation.Categorical(
            surface,
            "EVOKES_FRAME",
            concept,
            source,
            null,
            SourceTrust.StructuredCorpus));

        await using (var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource),
            pg.DataSource))
        {
            await writer.ApplyWorkingSetAsync(builder.Build());
        }

        // The selected semantic frame deliberately has no content body or canonical
        // display name of its own. REALIZE therefore has to leave semantic hash space
        // through the witnessed EVOKES_FRAME surface; returning NULL is the old defect.
        await using (var direct = pg.DataSource.CreateCommand(
            "SELECT (realize.batch(ARRAY[$1]::bytea[]))[1]"))
        {
            direct.Parameters.AddWithValue(NpgsqlDbType.Bytea, concept.ToBytes());
            Assert.Equal(DBNull.Value, await direct.ExecuteScalarAsync());
        }

        await using (var down = pg.DataSource.CreateCommand(
            "SELECT surface_id FROM taxonomy.bubble_down($1,NULL::bytea,1,NULL::bytea,0::numeric)"))
        {
            down.Parameters.AddWithValue(NpgsqlDbType.Bytea, concept.ToBytes());
            var projected = Assert.IsType<byte[]>(await down.ExecuteScalarAsync());
            Assert.Equal(surface.ToBytes(), projected);
        }

        await using (var realized = pg.DataSource.CreateCommand(
            "SELECT (realize.forward_text_batch(ARRAY[$1]::bytea[],NULL::bytea))[1]"))
        {
            realized.Parameters.AddWithValue(NpgsqlDbType.Bytea, concept.ToBytes());
            Assert.Equal(surfaceText, Assert.IsType<string>(await realized.ExecuteScalarAsync()));
        }
    }
}
