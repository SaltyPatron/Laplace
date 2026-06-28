using Xunit;

namespace Laplace.Modality.Chess.Tests;

/// <summary>
/// Strength proofs via our own in-process match harness (no cutechess): the search crushes a random
/// mover (verification bar: beats-random), and a deeper search beats a shallower one (the search
/// actually adds strength). Bounded game counts/depths keep these fast; the full ablation ladder
/// (`laplace chess ladder`) runs longer for tight Elo error bars. Pure C#, no native/DB.
/// </summary>
public sealed class MatchTests
{
    [Fact]
    public void Search_CrushesRandomMover()
    {
        var r = MatchRunner.Play(
            MatchRunner.Searcher(depth: 2), MatchRunner.RandomChooser,
            games: 20, maxPlies: 120, seed: 7);
        Assert.True(r.Score >= 0.90, $"depth-2 search vs random scored {r.Score:F3} ({r.AWins}-{r.Draws}-{r.BWins})");
    }

    [Fact]
    public void DeeperSearch_BeatsShallower()
    {
        var r = MatchRunner.Play(
            MatchRunner.Searcher(depth: 3), MatchRunner.Searcher(depth: 1),
            games: 12, maxPlies: 100, seed: 3);
        Assert.True(r.Score > 0.60, $"depth-3 vs depth-1 scored {r.Score:F3} ({r.AWins}-{r.Draws}-{r.BWins}); Elo +{r.EloDiff:F0}");
    }
}
