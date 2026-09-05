using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

public sealed class TrajectoryTests
{
    [Fact]
    public void Build_Then_Constituents_RoundTrips()
    {
        var ids = new Hash128[]
        {
            new(0x1122334455667788ul, 0x99AABBCCDDEEFF00ul),
            new(0xDEADBEEFCAFEF00Dul, 0x0123456789ABCDEFul),
            new(ulong.MaxValue, ulong.MaxValue),
            new(0ul, 0ul),
        };
        double[] xyzm = Trajectory.Build(ids);
        Assert.Equal(ids.Length * 4, xyzm.Length);

        Hash128[] back = Trajectory.Constituents(xyzm);
        Assert.Equal(ids, back);
    }

    [Fact]
    public void Empty_Is_Empty()
    {
        Assert.Empty(Trajectory.Build(ReadOnlySpan<Hash128>.Empty));
        Assert.Empty(Trajectory.Constituents(ReadOnlySpan<double>.Empty));
    }

    [Fact]
    public void RepeatedFlaggedConstituents_CompactAndExpandInOrder()
    {
        var space = new Hash128(1, 2);
        var token = new Hash128(3, 4);
        Hash128[] ids = [space, space, space, token, space, space];
        ulong atom = Trajectory.VertexFlags(0, hasAtom: true, atom: (uint)' ');
        ulong deep = Trajectory.VertexFlags(47, hasAtom: false, atom: 0);

        double[] xyzm = Trajectory.Build(ids, [atom, atom, atom, deep, atom, atom]);

        Assert.Equal(3 * 4, xyzm.Length);
        Assert.Equal(ids, Trajectory.Constituents(xyzm));
    }
}
