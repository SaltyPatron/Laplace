using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Substrate.Tests.Crud;

public sealed class EntityInterpretationChangeTests
{
    [Fact]
    public void SameContent_DifferentInterpretations_KeepOneCanonicalEntityAndEveryClaim()
    {
        var source = Hash128.Blake3("entity-interpretation/source"u8);
        var id = Hash128.Blake3("entity-interpretation/content"u8);
        var typeA = Hash128.Blake3("entity-interpretation/type-a"u8);
        var typeB = Hash128.Blake3("entity-interpretation/type-b"u8);

        using var builder = new SubstrateChangeBuilder(source, "entity-interpretation/test");
        builder.AddEntity(id, 3, typeB, source);
        builder.AddEntity(id, 1, typeA, source);
        builder.AddEntity(id, 1, typeA, source); // exact repeat is not a new observation shape

        var change = builder.Build();

        Assert.Single(change.Entities);
        Assert.Equal(id, change.Entities[0].Id);
        Assert.Equal(2, change.EntityInterpretations.Length);
        Assert.Contains(change.EntityInterpretations,
            row => row.EntityId == id && row.Tier == 1 && row.TypeId == typeA);
        Assert.Contains(change.EntityInterpretations,
            row => row.EntityId == id && row.Tier == 3 && row.TypeId == typeB);
    }

    [Fact]
    public void InterpretationArrivalOrder_DoesNotChangeIntentIdentity()
    {
        var source = Hash128.Blake3("entity-interpretation/order-source"u8);
        var id = Hash128.Blake3("entity-interpretation/order-content"u8);
        var typeA = Hash128.Blake3("entity-interpretation/order-type-a"u8);
        var typeB = Hash128.Blake3("entity-interpretation/order-type-b"u8);

        using var left = new SubstrateChangeBuilder(source, "entity-interpretation/order");
        left.AddEntity(id, 3, typeB, source);
        left.AddEntity(id, 1, typeA, source);
        var leftChange = left.Build();

        using var right = new SubstrateChangeBuilder(source, "entity-interpretation/order");
        right.AddEntity(id, 1, typeA, source);
        right.AddEntity(id, 3, typeB, source);
        var rightChange = right.Build();

        Assert.Equal(leftChange.Metadata.IntentId, rightChange.Metadata.IntentId);
        Assert.Single(leftChange.Entities);
        Assert.Single(rightChange.Entities);
        Assert.Equal(2, leftChange.EntityInterpretations.Length);
        Assert.Equal(2, rightChange.EntityInterpretations.Length);
    }
}
