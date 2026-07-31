using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// End-to-end over fixture PGNs: the analyzer's multi-ply motif pass must deposit the
// sacrifice family at BOTH grains — line grain via GAME_HAS_MOTIF (subject = line) and
// position grain via HAS_MOTIF (subject = position reached, ctx = null so every game
// reaching the position corroborates one cell).
[Trait("Tier", "fast")]
public sealed class ChessMotifDepositTests
{
    private const string KingsGambitAccepted =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. f4 exf4 3. Nf3 g5 1-0\n";

    private const string KingsGambitDeclined =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. f4 d6 3. Nf3 Nc6 1-0\n";

    private static readonly Hash128 GameMotifType = RelationTypeRegistry.RelationTypeId("GAME_HAS_MOTIF");
    private static readonly Hash128 PositionMotifType = RelationTypeRegistry.RelationTypeId("HAS_MOTIF");

    private static (SubstrateChange Change, ChessGameRecord Parsed) Analyze(string pgn)
    {
        CodepointPerfcache.LoadDefault();
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/motif-deposit");
        ChessAnalyze.DeriveFromParsed(b, parsed);
        return (b.SetInputUnitsConsumed(1).Build(), parsed);
    }

    private static bool HasGameMotif(SubstrateChange c, Hash128 lineId, string tag)
        => c.Attestations.Any(a => a.TypeId == GameMotifType
            && a.SubjectId == lineId && a.ObjectId == ContentEmitter.RootId(tag));

    private static bool HasPositionMotif(SubstrateChange c, string tag)
        => c.Attestations.Any(a => a.TypeId == PositionMotifType
            && a.ObjectId == ContentEmitter.RootId(tag) && a.ContextId is null);

    [Fact]
    public void AcceptedGambit_DepositsGambitAndSacrifice_AtBothGrains()
    {
        var (change, parsed) = Analyze(KingsGambitAccepted);
        Assert.True(HasGameMotif(change, parsed.LineId, "gambit"));
        Assert.True(HasGameMotif(change, parsed.LineId, "sacrifice"));
        Assert.False(HasGameMotif(change, parsed.LineId, "sacrifice_offered"));
        Assert.True(HasPositionMotif(change, "gambit"));
        Assert.True(HasPositionMotif(change, "sacrifice"));
    }

    [Fact]
    public void DeclinedGambit_DepositsOfferOnly()
    {
        var (change, parsed) = Analyze(KingsGambitDeclined);
        Assert.True(HasGameMotif(change, parsed.LineId, "gambit"));
        Assert.True(HasGameMotif(change, parsed.LineId, "sacrifice_offered"));
        Assert.False(HasGameMotif(change, parsed.LineId, "sacrifice"));
        Assert.True(HasPositionMotif(change, "sacrifice_offered"));
    }

    [Fact]
    public void PositionGrainMotifs_RideTheAnalysisSource()
    {
        var (change, _) = Analyze(KingsGambitAccepted);
        var rows = change.Attestations.Where(a => a.TypeId == PositionMotifType).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, a => Assert.Equal(ChessAnalyze.SourceId, a.SourceId));
    }
}
