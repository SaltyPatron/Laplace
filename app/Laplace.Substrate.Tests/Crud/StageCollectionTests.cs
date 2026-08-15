using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Substrate.Tests.Crud;

/// <summary>
/// The laws a set composition rests on. Each assertion here is the reason a set can be stored
/// as ONE entity instead of a fan of edges (docs/specs/38); none of them is a preference.
/// </summary>
public class StageCollectionTests
{
    private static readonly Hash128 Src = Hash128.OfCanonical("StageCollectionTests");
    private static readonly Hash128 Type = Hash128.OfCanonical("Collection");

    private static SubstrateChangeBuilder NewBuilder() => new(Src, "test-unit");

    private static Hash128 Member(string s) => Hash128.OfCanonical(s);

    // Distinct, non-origin placements. The values do not matter; that they are per-member and
    // never zero does — the origin is not a placement and would forge a centroid.
    private static double[] Coords(int n)
    {
        var c = new double[n * 4];
        for (int i = 0; i < n; i++)
        {
            c[i * 4 + 0] = 0.1 + i * 0.05;
            c[i * 4 + 1] = 0.2 - i * 0.03;
            c[i * 4 + 2] = 0.3 + i * 0.01;
            c[i * 4 + 3] = 0.4 - i * 0.02;
        }
        return c;
    }

    [Fact]
    public void SetIdentityIsIndependentOfMemberOrder()
    {
        Hash128 a = Member("nominative"), b = Member("singular"), c = Member("masculine");
        var coords = Coords(3);

        // Same members, three arrival orders, carrying their own coordinates through the sort.
        var id1 = NewBuilder().StageCollection([a, b, c], coords, 3, Type, Src);
        var id2 = NewBuilder().StageCollection(
            [c, b, a], [.. coords[8..12], .. coords[4..8], .. coords[0..4]], 3, Type, Src);
        var id3 = NewBuilder().StageCollection(
            [b, a, c], [.. coords[4..8], .. coords[0..4], .. coords[8..12]], 3, Type, Src);

        Assert.Equal(id1, id2);
        Assert.Equal(id1, id3);
    }

    [Fact]
    public void DuplicateMembersCollapseToTheSameSet()
    {
        Hash128 a = Member("plural"), b = Member("genitive");
        var two = Coords(2);
        var id = NewBuilder().StageCollection([a, b], two, 3, Type, Src);
        var dup = NewBuilder().StageCollection(
            [a, b, a], [.. two, .. two[0..4]], 3, Type, Src);
        Assert.Equal(id, dup);
    }

    [Fact]
    public void SingleMemberCollapsesToTheMemberAndStagesNothing()
    {
        // The tier-floor law (hash128.c:22): a one-child composition IS its child. A degenerate
        // one-tag "set" must stay a direct edge to that tag, never a wrapper entity.
        Hash128 only = Member("plural");
        var b = NewBuilder();
        var id = b.StageCollection([only], Coords(1), 3, Type, Src);

        Assert.Equal(only, id);
        var change = b.Build();
        Assert.Empty(change.Physicalities);
        Assert.Empty(change.Entities);
    }

    [Fact]
    public void SetStagesOneEntityAndOneSetPhysicalityCarryingEveryMember()
    {
        Hash128 a = Member("nominative"), b = Member("singular"), c = Member("masculine");
        var builder = NewBuilder();
        var id = builder.StageCollection([a, b, c], Coords(3), 3, Type, Src);
        var change = builder.Build();

        var entity = Assert.Single(change.Entities);
        Assert.Equal(id, entity.Id);

        var phys = Assert.Single(change.Physicalities);
        Assert.Equal(PhysicalityType.Set, phys.Type);
        Assert.Equal(id, phys.EntityId);
        Assert.Equal(3, phys.NConstituents);
        Assert.NotNull(phys.TrajectoryXyzm);
        Assert.Equal(3 * 4, phys.TrajectoryXyzm!.Length);
    }

    [Fact]
    public void DisjointSetsOverTheSameVocabularyGetDistinctIds()
    {
        Hash128 a = Member("nominative"), b = Member("singular"), c = Member("plural");
        var two = Coords(2);
        var ab = NewBuilder().StageCollection([a, b], two, 3, Type, Src);
        var ac = NewBuilder().StageCollection([a, c], two, 3, Type, Src);
        Assert.NotEqual(ab, ac);
    }

    [Fact]
    public void MemberCoordinateMismatchIsRejected()
    {
        var b = NewBuilder();
        Assert.Throws<ArgumentException>(() =>
            b.StageCollection([Member("a"), Member("b")], Coords(1), 3, Type, Src));
    }

    [Fact]
    public void EmptySetIsRejected()
    {
        var b = NewBuilder();
        Assert.Throws<ArgumentException>(() =>
            b.StageCollection(ReadOnlySpan<Hash128>.Empty, [], 3, Type, Src));
    }

    [Fact]
    public void MemberWithNoStagedPhysicalityThrowsRatherThanForgingACoordinate()
    {
        var b = NewBuilder();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            b.StageCollection([Member("a"), Member("b")], 3, Type, Src));
        Assert.Contains("no physicality staged", ex.Message);
    }
}
