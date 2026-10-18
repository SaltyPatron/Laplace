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
        Assert.Equal(
            "Example Player reached this position and played Nf3, not Bc4.",
            ChessMoveCommentary.FormatUndated("Example Player", "Nf3", "Bc4"));
    }

    [Fact]
    public void Salience_OrdersByLowerBoundThenEloThenOpening_NotByDate()
    {
        int strongFirst = ChessMoveCommentary.CompareSalience(
            effMu: 1_600_000_000_000d, elo: 2200, openingOrMotif: 0,
            otherEffMu: 1_200_000_000_000d, otherElo: 2700, otherOpeningOrMotif: 1);
        Assert.True(strongFirst < 0);

        int eloBreaksTheTie = ChessMoveCommentary.CompareSalience(
            effMu: null, elo: 2500, openingOrMotif: 0,
            otherEffMu: null, otherElo: 2000, otherOpeningOrMotif: 1);
        Assert.True(eloBreaksTheTie < 0);

        int openingBreaksTheTie = ChessMoveCommentary.CompareSalience(
            effMu: 1_500_000_000_000d, elo: 2400, openingOrMotif: 1,
            otherEffMu: 1_500_000_000_000d, otherElo: 2400, otherOpeningOrMotif: 0);
        Assert.True(openingBreaksTheTie < 0);
    }

    [Fact]
    public void PlayerPrecedent_RequiresAMasterLowerBoundOrElo()
    {
        Assert.False(ChessMoveCommentary.IsSalientPlayer(effMu: null, elo: null));
        Assert.False(ChessMoveCommentary.IsSalientPlayer(effMu: 800_000_000_000d, elo: 1800));
        Assert.True(ChessMoveCommentary.IsSalientPlayer(effMu: null, elo: 2400));
        Assert.True(ChessMoveCommentary.IsSalientPlayer(
            effMu: Laplace.Modality.GlickoPriors.NeutralMu, elo: null));
    }

    [Fact]
    public void Comment_StatesOpeningDeltaVariationAndOnlyASalientPrecedent()
    {
        string text = ChessMoveCommentary.Compose(
            opening: "King's Gambit: f4",
            delta: "Gambit",
            pv: ["e2e4", "e7e5", "f2f4"],
            evalLine: "Eval +0.3 (d8)",
            precedent: "1972: Tal had this exact position and played f4.",
            book: null,
            population: "Next f4 6, Nf3 2",
            maxChars: ChessMoveCommentary.LichessMaxChars);

        Assert.Contains("King's Gambit: f4", text);
        Assert.Contains("Gambit", text);
        Assert.Contains("PV e2e4 e7e5 f2f4", text);
        Assert.Contains("1972: Tal", text);
        Assert.Contains("Next f4 6", text);

        string withoutPrecedent = ChessMoveCommentary.Compose(
            "Sicilian: c5", "Fork", ["d2d4"], "Eval +0.2 (d6)",
            precedent: null, book: null, population: "4 games reached this position",
            maxChars: 140);
        Assert.DoesNotContain("Tal", withoutPrecedent);
        Assert.Contains("4 games reached this position", withoutPrecedent);
    }

    [Fact]
    public void Distribution_StatesTheFullMoveSplitOnlyWhenEveryWitnessIsCounted()
    {
        Assert.Equal(
            "Next Nf3 3, Bc4 1",
            ChessMoveCommentary.FormatDistribution([("Nf3", 3), ("Bc4", 1)], witnesses: 4));
        Assert.Equal(
            "9 games reached this position",
            ChessMoveCommentary.FormatDistribution([("Nf3", 2)], witnesses: 9));
    }

    [Fact]
    public void BookFragment_IsNotAPrecedent()
    {
        Assert.False(ChessMoveCommentary.IsSalientBook("short"));
        Assert.True(ChessMoveCommentary.IsSalientBook("The gambit is the point of the line."));
    }
}
