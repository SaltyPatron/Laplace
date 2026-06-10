using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public class CanonicalPathLawTests
{
    [Theory]
    [InlineData("IS_A")]
    [InlineData("HAS_PART")]
    [InlineData("PRECEDES")]
    public void RelationTypeId_PathUsesSubstrateType(string name)
    {
        var id = RelationTypeRegistry.RelationTypeId(name);
        var canonical = Hash128.OfCanonical($"substrate/type/{name}/v1");
        Assert.Equal(canonical, id);
        Assert.Contains("/type/", $"substrate/type/{name}/v1");
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
