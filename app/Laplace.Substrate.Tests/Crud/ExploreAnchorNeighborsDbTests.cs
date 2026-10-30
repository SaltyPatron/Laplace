using System.Globalization;
using Laplace.Ingestion;
using Npgsql;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class ExploreAnchorNeighborsDbTests(LocalPgFixture pg)
{
    [Fact]
    public async Task ShapeNeighborsUseCanonicalContentAndReturnCanonicalHeight()
    {
        await using var connection = await pg.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var migrate = NpgsqlCatalog.Command(connection, transaction, "migration.canonical_substrate"))
            await migrate.ExecuteNonQueryAsync();
        using var window = new StagedSourceWriter(1024, 4096, 1 << 20);
        var word = window.Text("mississippi"u8);
        var number = window.Text("3"u8);
        var writer = new NpgsqlSubstrateWriter(pg.DataSource);
        await writer.ApplyCanonicalWorkingSetAsync(window, connection, transaction, [word.Id]);
        await using var anchor = new NpgsqlCommand("""
            SELECT public.ST_X(e.coord),public.ST_Y(e.coord),public.ST_Z(e.coord),public.ST_M(e.coord),
                public.ST_AsText(structural.entity_curve($1),53),public.ST_NPoints(structural.entity_curve($1))
            FROM public.laplace_entities(ARRAY[encode($1,'hex')::public.blake3]) e
            """, connection, transaction);
        anchor.Parameters.AddWithValue(word.Id.ToBytes());
        double[] coordinates; string trajectory;
        await using (var row = await anchor.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            coordinates = Enumerable.Range(0,4).Select(row.GetDouble).ToArray();
            trajectory = row.GetString(4);
            Assert.Equal(11, row.GetInt32(5));
        }
        var neighbors = await NpgsqlSubstrateReads.ExploreAnchorNeighborsAsync(
            connection, coordinates[0], coordinates[1], coordinates[2], coordinates[3],
            trajectory, 0, 100, 4, 60, CancellationToken.None);
        var shape = neighbors.Where(n => n.Axis == "shape").ToArray();
        var selected = Assert.Single(shape, n => n.IdHex == word.Id.ToHex());
        Assert.Equal((short)word.Tier, selected.Tier);
        Assert.Equal(0d, selected.Frechet);
        Assert.DoesNotContain(shape, n => n.IdHex == number.Id.ToHex());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task BrowsePreservesWordRoleAndCanonicalHeight()
    {
        CodepointPerfcache.LoadDefault();
        string unit = $"browse-word-role/{Guid.NewGuid():N}";
        Hash128 source = Hash128.Text(unit);
        using var builder = new SubstrateChangeBuilder(source, unit)
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
        Assert.NotNull(ContentEmitter.Emit(builder, unit, source));
        using var tree = TextDecomposer.Run("mississippi");
        var word = StageRoot(builder, tree, EntityTypeRegistry.Word, source);
        await using (var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource))
            await writer.ApplyWorkingSetAsync(builder.Build());
        await using var connection = await pg.DataSource.OpenConnectionAsync();
        var rows = await NpgsqlBrowseReads.NamedEntitiesAsync(connection,
            [word.Id.ToBytes()], word.Id.ToBytes(), 0, 20, 0, CancellationToken.None);
        var selected = Assert.Single(rows, r => r.IdHex == word.Id.ToHex());
        Assert.Equal((short)1, selected.Tier);
        Assert.Equal("word", selected.Type);
        Assert.Equal("surface", selected.MatchKind);
    }

    private static OrderedCompositionResult StageRoot(
        SubstrateChangeBuilder builder, TierTree tree, Hash128 role, Hash128 source)
    {
        var result = new OrderedCompositionResult[1];
        OrderedComposition.StageBatch(builder.ContentStage,
            [new OrderedCompositionRequest(Components(tree), role, source, 0)], result);
        var root = tree.GetNode(tree.NaturalUnitIndex());
        Assert.Equal(root.Id, result[0].Id);
        Assert.Equal(root.Tier, result[0].Tier);
        return result[0];
    }

    private static unsafe OrderedCompositionComponent[] Components(TierTree tree)
    {
        var root = tree.GetNode(tree.NaturalUnitIndex());
        var components = new OrderedCompositionComponent[root.ChildCount];
        for (uint i = 0; i < root.ChildCount; ++i)
        {
            var child = tree.GetNode(tree.CollapseIndex(root.FirstChildIdx + i));
            components[i] = new OrderedCompositionComponent(child.Id, child.Tier,
                child.Coord[0], child.Coord[1], child.Coord[2], child.Coord[3],
                child.Atom, child.Tier == 0);
        }
        return components;
    }
}
