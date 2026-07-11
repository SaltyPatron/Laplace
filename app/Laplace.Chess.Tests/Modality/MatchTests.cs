using Xunit;

namespace Laplace.Modality.Chess.Tests;

public sealed class MatchTests
{
    [Fact]
    [Trait("Tier", "perf")]
    public void Search_CrushesRandomMover()
    {
        var r = MatchRunner.Play(
            MatchRunner.Searcher(depth: 2), MatchRunner.RandomChooser,
            games: 20, maxPlies: 120, seed: 7);
        Assert.True(r.Score >= 0.90, $"depth-2 search vs random scored {r.Score:F3} ({r.AWins}-{r.Draws}-{r.BWins})");
    }

    [Fact]
    [Trait("Tier", "perf")]
    public void DeeperSearch_BeatsShallower()
    {
        var r = MatchRunner.Play(
            MatchRunner.Searcher(depth: 3), MatchRunner.Searcher(depth: 1),
            games: 12, maxPlies: 100, seed: 3);
        Assert.True(r.Score > 0.60, $"depth-3 vs depth-1 scored {r.Score:F3} ({r.AWins}-{r.Draws}-{r.BWins}); Elo +{r.EloDiff:F0}");
    }
}
