using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Engine.Core.Tests;

/// <summary>The content-storage codec must be exactly reversible through the
/// C# boundary — it's what stores + replays composition for the DB round-trip.</summary>
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
}
