using Laplace.Chess.Service;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class CutechessRunnerTests
{
    [Fact]
    public void ParseLines_ExtractsScoreAndElo()
    {
        var lines = new[]
        {
            "Score of Laplace vs Stockfish: 6 - 2 - 2",
            "Elo difference: 42.5 +/- 12.3",
        };
        var events = CutechessRunner.ParseLinesForTest(lines).ToList();
        Assert.Contains(events, e => e is ChessLabProgressEvent);
        Assert.Contains(events, e => e is ChessLabMetricEvent m && m.Name == "elo_diff" && m.Value > 40);
    }
}
