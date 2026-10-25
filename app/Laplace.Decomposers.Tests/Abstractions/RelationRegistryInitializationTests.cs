using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Tests.Abstractions;

[Collection("GrammarPerfcache")]
public sealed class RelationRegistryInitializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstDynamicRelationLookupLoadsItsRequiredFloorAndNeverPublishesAZeroId(bool surface)
    {
        string name = surface ? "first-use/surface-relation" : "first-use/type-relation";
        CodepointPerfcache.Unload();
        try
        {
            Hash128 id = surface ? RelationTypeRegistry.Resolve(name).Id : RelationTypeRegistry.RelationTypeId(name);
            Assert.NotEqual(default, id);
            // Native owns the text floor and may already hold its own mapping.
            // A managed view need not be published to resolve a content identity.
            Assert.Equal(ContentEmitter.RootId(name)!.Value, id);
            Assert.Equal(id, RelationTypeRegistry.RelationTypeId(name));
            Assert.Equal(id, RelationTypeRegistry.Resolve(name).Id);
        }
        finally { CodepointPerfcache.LoadDefault(); }
    }
}
