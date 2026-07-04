using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.SubstrateCRUD.Tests;

// Issue 25's residual duplication (grapheme 's' had two physicality rows -- one atomic from
// BuildTier0Seed, one from a composed path that built a redundant length-1 trajectory) was
// originally patched by gating trajectory-building on ChildCount>1 so the two float-hashes
// coincided. That gate only covered the empty-trajectory case; entities with divergent
// NON-empty trajectories (e.g. 319 chess-move tokens) still forked. The real fix makes
// physicality identity CONTENT-derived: physId = hash(entityId, type), independent of the
// coord/trajectory geometry entirely. So a composed physicality and the atomic seed for the
// same content now produce the same id STRUCTURALLY -- there is no float path to diverge on.
public class PhysicalityIdRegressionTests
{
    [Fact]
    public void PhysicalityId_IsContentDerived_IndependentOfGeometry()
    {
        var entityId = Hash128.FromBytes(Convert.FromHexString("3d1d92230feb6db469532f26d9e2d7ab"));

        // Same (entity, type) -> same id, regardless of how the caller arrived at any geometry.
        var a = PhysicalityId.Compute(entityId, PhysicalityType.Content);
        var b = PhysicalityId.Compute(entityId, PhysicalityType.Content);
        Assert.Equal(a, b);

        // Type participates in identity so distinct physicality roles never collide.
        Assert.NotEqual(a, PhysicalityId.Compute(entityId, PhysicalityType.BuildingBlock));

        // Different content -> different id (entity_id is the only content input).
        var other = Hash128.FromBytes(Convert.FromHexString("00112233445566778899aabbccddeeff"));
        Assert.NotEqual(a, PhysicalityId.Compute(other, PhysicalityType.Content));
    }
}
