using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// End-to-end over fixture PGNs: the analyzer's multi-ply motif pass must deposit the
// sacrifice family once at line grain. Exact-board motif projections duplicate a property
// already recoverable from the played trajectory and create singleton consensus cells.
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

    [Fact]
    public void AcceptedGambit_DepositsGambitAndSacrifice_OnceOnLine()
    {
        var (change, parsed) = Analyze(KingsGambitAccepted);
        Assert.True(HasGameMotif(change, parsed.LineId, "gambit"));
        Assert.True(HasGameMotif(change, parsed.LineId, "sacrifice"));
        Assert.False(HasGameMotif(change, parsed.LineId, "sacrifice_offered"));
    }

    [Fact]
    public void DeclinedGambit_DepositsOfferOnly()
    {
        var (change, parsed) = Analyze(KingsGambitDeclined);
        Assert.True(HasGameMotif(change, parsed.LineId, "gambit"));
        Assert.True(HasGameMotif(change, parsed.LineId, "sacrifice_offered"));
        Assert.False(HasGameMotif(change, parsed.LineId, "sacrifice"));
    }

    [Fact]
    public void ExactPositionMotifProjection_IsAbsent()
    {
        var (change, _) = Analyze(KingsGambitAccepted);
        var positionMotifType = RelationTypeRegistry.RelationTypeId("HAS_MOTIF");
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == positionMotifType);
    }
}
