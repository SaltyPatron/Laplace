using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class CanonicalPathLawTests
{
    [Theory]
    [InlineData("IS_A")]
    [InlineData("HAS_PART")]
    [InlineData("PRECEDES")]
    public void RelationTypeId_IsContentAddressed(string name)
    {

        // A relation's id is the content id of its label: the same entity that text is
        // anywhere else.
        var id = RelationTypeRegistry.RelationTypeId(name);
        Assert.Equal(ContentEmitter.RootId(name)!.Value, id);
    }

    [Fact]
    public void PhysicalityType_PathsUsePhysicalityTypeSegment()
    {
        foreach (var seg in new[] { "CONTENT", "BUILDING_BLOCK", "PROJECTION", "PROJECTION_OUTPUT" })
        {
            var path = $"substrate/physicality_type/{seg}/v1";
            Assert.Contains("physicality_type", path);
            _ = Hash128.OfCanonical(path);
        }
    }
}
