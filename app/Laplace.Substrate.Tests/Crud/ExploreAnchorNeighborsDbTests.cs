using System.Globalization;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

[Collection("substrate-pg")]
[Trait("Tier", "db")]
public sealed class ExploreAnchorNeighborsDbTests(LocalPgFixture pg)
{
    [Fact]
    public async Task ShapeNeighborsUseWordRoleAndReturnCanonicalHeight()
    {
        CodepointPerfcache.LoadDefault();
        string unit = $"explore-word-role/{Guid.NewGuid():N}";
        Hash128 source = Hash128.Text(unit);
        using var builder = new SubstrateChangeBuilder(source, unit)
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
        Assert.NotNull(ContentEmitter.Emit(builder, unit, source));

        using var wordTree = TextDecomposer.Run("mississippi");
        var word = StageRoot(builder, wordTree, EntityTypeRegistry.Word, source);
        Assert.Equal(1, word.Tier);
        using var digitsTree = TextDecomposer.Run("255");
        StageRoot(builder, digitsTree, EntityTypeRegistry.Id("Number"), source);
        using var numberTree = TextDecomposer.Run("-255");
        var number = StageRoot(builder, numberTree, EntityTypeRegistry.Id("Number"), source);
        Assert.Equal(2, number.Tier);

        await using (var writer = new ConsensusAccumulatingWriter(
            new NpgsqlSubstrateWriter(pg.DataSource), pg.DataSource))
            await writer.ApplyWorkingSetAsync(builder.Build());

        // Transport the actual Native child coordinates, never packed ID carriers.
        string trajectory = "LINESTRING ZM (" + string.Join(",", Components(wordTree).Select(c =>
            string.Join(" ", new[] { c.CoordX, c.CoordY, c.CoordZ, c.CoordM }
                .Select(v => v.ToString("R", CultureInfo.InvariantCulture))))) + ")";
        await using var connection = await pg.DataSource.OpenConnectionAsync();
        var reader = new NpgsqlSubstrateReader(pg.DataSource);
        Assert.Equal(1, await reader.CountEntitiesByTypeAsync(EntityTypeRegistry.Word));
        var placement = await NpgsqlSubstrateReads.EntityPrimaryFormAsync(
            connection, word.Id.ToBytes(), CancellationToken.None);
        Assert.NotNull(placement);
        Assert.Equal(11, placement.Value.Constituents);
        var neighbors = await NpgsqlSubstrateReads.ExploreAnchorNeighborsAsync(
            connection, word.CoordX, word.CoordY, word.CoordZ, word.CoordM,
            trajectory, 0, 100, 4, 60, CancellationToken.None);
        var shape = neighbors.Where(n => n.Axis == "shape").ToArray();
        var selected = Assert.Single(shape, n => n.IdHex == word.Id.ToHex());
        Assert.Equal((short)word.Tier, selected.Tier);
        Assert.Equal(0d, selected.Frechet);
        Assert.DoesNotContain(shape, n => n.IdHex == number.Id.ToHex());
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
