using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessMoveCommentaryContextTests
{
    [Fact]
    public void ForkMotif_StaysInChessDomain()
    {
        Assert.Equal("Fork", ChessMoveCommentary.MotifLabel("fork"));
    }

    [Fact]
    public void HistoricalLine_UsesWitnessedDatePlayerAndMove()
    {
        var line = ChessMoveCommentary.FormatHistorical(
            "1972.07.11", "Example Player", "Nf3", "Bc4");

        Assert.Equal(
            "1972: Example Player had this exact position and played Nf3, not Bc4.",
            line);
    }

    [Fact]
    public void HistoricalLine_DoesNotInventMissingYear()
    {
        Assert.Null(ChessMoveCommentary.FormatHistorical(
            null, "Example Player", "Nf3", "Bc4"));
    }
}
