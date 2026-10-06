using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Endpoints.Lichess.Tests;

public sealed class ChallengePolicyTests
{
    [Fact]
    public void DefaultIsCasualOnlyAtEverySpeed()
    {
        var p = new LichessChallengePolicy();
        Assert.True(p.Accepts("blitz", rated: false));
        Assert.True(p.Accepts("classical", rated: false));
        Assert.False(p.Accepts("blitz", rated: true));
    }

    [Fact]
    public void RatedAndSpeedsAreIndependentFilters()
    {
        var p = new LichessChallengePolicy(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rapid" }, Rated: true);
        Assert.True(p.Accepts("rapid", rated: true));
        Assert.True(p.Accepts("rapid", rated: false));
        Assert.False(p.Accepts("bullet", rated: false));
    }
}
