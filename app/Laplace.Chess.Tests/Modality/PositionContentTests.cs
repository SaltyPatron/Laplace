using Laplace.Modality.Chess;
using Xunit;

namespace Laplace.Modality.Chess.Tests;

public sealed class PositionContentTests
{
    [Fact]
    public void Rekey_AppendsFeatureTokens_WhenEnabled()
    {
        PositionContent.IncludeFeatureTokens = true;
        try
        {
            var b = Board.FromFen("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1");
            string s = PositionContent.Surface(b, "e3");
            Assert.Contains(" mob:", s);
        }
        finally
        {
            PositionContent.IncludeFeatureTokens = false;
        }
    }

    [Fact]
    public void Rekey_OmitsFeatureTokens_ByDefault()
    {
        PositionContent.IncludeFeatureTokens = false;
        var b = Board.FromFen("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1");
        string s = PositionContent.Surface(b, "e3");
        Assert.DoesNotContain(" mob:", s);
    }
}
