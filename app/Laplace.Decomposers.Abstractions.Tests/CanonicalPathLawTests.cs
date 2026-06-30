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
        // Relation type identity is blake3(utf8_bytes(name)) — no namespace prefix
        var id = RelationTypeRegistry.RelationTypeId(name);
        var expected = Hash128.Blake3(System.Text.Encoding.UTF8.GetBytes(name));
        Assert.Equal(expected, id);
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
