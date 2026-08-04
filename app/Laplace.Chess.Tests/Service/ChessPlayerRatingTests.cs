using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

/// <summary>
/// A player's record is a FOLDED CELL, not a query-time aggregate.
///
/// It used to be a GROUP BY over every colour-header row in the corpus (~400k rows, ~10s),
/// cached with a TTL and a prewarm — a cache standing in for a missing fold. These pin the
/// edges that make the record a read: the rating math this system is built on is the same
/// math invented to rate chess players, so the player is the one subject it should never
/// have had to recompute.
/// </summary>
public sealed class ChessPlayerRatingTests
{
    private const string WhiteWins =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    private static readonly Hash128 Alice = ChessVocabulary.PlayerId("Alice");
    private static readonly Hash128 Bob = ChessVocabulary.PlayerId("Bob");
    private static readonly Hash128 Outcome = EntityTypeRegistry.Id("OUTCOME");
    private static readonly Hash128 PlayedBy = EntityTypeRegistry.Id("PLAYED_BY");

    private static SubstrateChange Compose(string pgn)
    {
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/pgn");
        ChessPgnDecomposer.ComposeGame(parsed, b, analyzeInline: false);
        return b.SetInputUnitsConsumed(1).Build();
    }

    private static AttestationRow Single(SubstrateChange c, Hash128 subject, Hash128 type)
        => Assert.Single(c.Attestations, a => a.SubjectId == subject && a.TypeId == type);

    [Fact]
    public void BothPlayers_GetAnOutcomeCell()
    {
        var change = Compose(WhiteWins);
        // One cell per player — the leaderboard's sort key, not a scan.
        Assert.NotEqual(default, Single(change, Alice, Outcome).Id);
        Assert.NotEqual(default, Single(change, Bob, Outcome).Id);
    }

    [Fact]
    public void TheWinnerAndLoserFoldOppositeScores()
    {
        var change = Compose(WhiteWins);
        var win = Single(change, Alice, Outcome);
        var loss = Single(change, Bob, Outcome);

        // Bit-identical to PlyOutcome by design: the same three constants that rate a chess
        // player rate every other epistemic claim in the substrate.
        Assert.Equal(Glicko2.ScoreWin, win.SumScoreFp1e9);
        Assert.Equal(Glicko2.ScoreLoss, loss.SumScoreFp1e9);
        Assert.Equal(1, win.ObservationCount);
    }

    [Fact]
    public void HeadToHead_IsItsOwnFoldedCell_BothDirections()
    {
        var change = Compose(WhiteWins);
        // PLAYED_BY was declared in the manifest and never emitted; this is the edge it was
        // reserved for. Each direction carries that player's own result.
        var aliceVsBob = Assert.Single(change.Attestations,
            a => a.SubjectId == Alice && a.TypeId == PlayedBy && a.ObjectId == Bob);
        var bobVsAlice = Assert.Single(change.Attestations,
            a => a.SubjectId == Bob && a.TypeId == PlayedBy && a.ObjectId == Alice);
        Assert.Equal(Glicko2.ScoreWin, aliceVsBob.SumScoreFp1e9);
        Assert.Equal(Glicko2.ScoreLoss, bobVsAlice.SumScoreFp1e9);
    }

    [Fact]
    public void ProvenanceStaysPerGame()
    {
        var change = Compose(WhiteWins);
        // GH #736: provenance context is the PLAYING (the event handle), never the line.
        var eventId = ChessPgnDecomposer.TryParseGame(WhiteWins)!.PlayingId;
        // The consensus cell aggregates; the evidence row still says which playing it came
        // from, so a rating can always be walked back to the games that made it.
        Assert.Equal(eventId, Single(change, Alice, Outcome).ContextId);
        Assert.Equal(eventId, Single(change, Bob, Outcome).ContextId);
    }

    [Fact]
    public void TheRecordEdgesAreStillThere()
    {
        var change = Compose(WhiteWins);
        // The aggregating lane is IN ADDITION to the witnessed record, never instead of it:
        // who sat where is still one categorical row per game.
        Assert.Contains(change.Attestations,
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_WHITE") && a.ObjectId == Alice);
        Assert.Contains(change.Attestations,
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_BLACK") && a.ObjectId == Bob);
    }

    [Fact]
    public void ADraw_FoldsHalfForBoth()
    {
        var drawn = WhiteWins.Replace("[Result \"1-0\"]", "[Result \"1/2-1/2\"]")
                             .Replace("Qxf7# 1-0", "Qxf7# 1/2-1/2");
        var change = Compose(drawn);
        Assert.Equal(Glicko2.ScoreDraw, Single(change, Alice, Outcome).SumScoreFp1e9);
        Assert.Equal(Glicko2.ScoreDraw, Single(change, Bob, Outcome).SumScoreFp1e9);
    }
}
