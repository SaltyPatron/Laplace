using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Substrate.Tests.Crud;

public sealed class PhysicalityContentIdentityTests
{
    private static readonly Hash128 Source = Hash128.OfCanonical("test/content-identity/source");
    private static readonly double[] Coord = [1, 0, 0, 0];

    private static PhysicalityRow Row(
        Hash128 entityId, PhysicalityType type, double[]? trajectory, int count,
        Hash128? physicalityId = null) => new(
            physicalityId ?? PhysicalityId.Compute(entityId, type),
            entityId, Source, type,
            Coord[0], Coord[1], Coord[2], Coord[3], Hilbert128.Encode(Coord),
            trajectory, count, null, null, 0);

    [Fact]
    public void MultiChildContentMustEqualExactOrderedMerkle()
    {
        Hash128[] children =
        [Hash128.OfCanonical("child/a"), Hash128.OfCanonical("child/b")];
        Hash128 parent = Hash128.Merkle(9, children);
        var row = Row(parent, PhysicalityType.Content, Trajectory.Build(children), children.Length);
        Assert.Equal(parent, row.EntityId);
    }

    [Fact]
    public void OneChildContentCollapsesToChildIdentity()
    {
        Hash128 child = Hash128.OfCanonical("child/only");
        var row = Row(child, PhysicalityType.Content, Trajectory.Build([child]), 1);
        Assert.Equal(child, row.EntityId);
    }

    [Fact]
    public void ArbitraryParentCannotMasqueradeAsContentTrajectory()
    {
        Hash128[] children =
        [Hash128.OfCanonical("child/a"), Hash128.OfCanonical("child/b")];
        Hash128 falseParent = Hash128.OfCanonical("source-specific/handle");
        Assert.Throws<InvalidOperationException>(() =>
            Row(falseParent, PhysicalityType.Content, Trajectory.Build(children), children.Length));
    }

    [Fact]
    public void ContentDeclaredCountMustMatchExpandedRleManifest()
    {
        Hash128 child = Hash128.OfCanonical("child/repeated");
        Hash128[] children = [child, child, child];
        Hash128 parent = Hash128.Merkle(0, children);
        double[] rle = Trajectory.Build(children);
        Assert.True(rle.Length < children.Length * 4);
        Assert.Throws<InvalidOperationException>(() =>
            Row(parent, PhysicalityType.Content, rle, count: 2));
    }

    [Fact]
    public void StructuralTrajectoryMayProjectGovernedIdentity()
    {
        Hash128 handle = Hash128.OfCanonical("governed/record");
        Hash128[] members =
        [Hash128.OfCanonical("schema"), Hash128.OfCanonical("offset")];
        var row = Row(handle, PhysicalityType.ParseStructure, Trajectory.Build(members), members.Length);
        Assert.Equal(handle, row.EntityId);
        Assert.Equal(PhysicalityType.ParseStructure, row.Type);
    }

    [Fact]
    public void PhysicalityIdMustMatchEntityAndType()
    {
        Hash128 entity = Hash128.OfCanonical("entity");
        Hash128 wrong = PhysicalityId.Compute(entity, PhysicalityType.Content);
        Assert.Throws<InvalidOperationException>(() =>
            Row(entity, PhysicalityType.Projection, null, 0, wrong));
    }
}
