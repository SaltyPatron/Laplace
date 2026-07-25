using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// GH #600: `laplace ingest chess` records the witnessed layer AND derives the calculated
// layer (positions, move edges, ANALYZED_AT marker) in ONE fused Compose pass, reusing the
// in-memory parse — no second Postgres hydrate + re-parse. These pin that the fused pass
// emits both layers together, and that --no-analyze still yields the pure game-grain record.
public sealed class ChessFusedIngestTests
{
    private const string Game =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    private static SubstrateChange Compose(bool analyzeInline)
    {
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "test/pgn");
        ChessPgnDecomposer.ComposeGame(parsed, b, analyzeInline);
        return b.SetInputUnitsConsumed(1).Build();
    }

    [Fact]
    public void FusedCompose_EmitsWitnessedAndDerivedLayersTogether()
    {
        var change = Compose(analyzeInline: true);

        // Witnessed layer intact: the game entity and its verbatim movetext.
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        // The movetext composes from its OWN ply tokens, not prose fragments — resolved
        // through the decomposer's own definition so test and writer cannot drift.
        var movetextId = ChessPgnDecomposer.MovetextId(ChessPgnDecomposer.MovetextSection(Game));
        Assert.Contains(change.Attestations, a =>
            a.ObjectId == movetextId && a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_MOVETEXT"));

        // Derived layer present in the SAME change: replayed positions, their geometry, and
        // the version watermark that makes the standalone analyzer scan skip this game.
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.PositionType);
        Assert.False(change.Physicalities.IsDefaultOrEmpty || change.Physicalities.Length == 0,
            "fused pass must compose position geometry");
        Assert.Contains(change.Attestations, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("ANALYZED_AT"));
    }

    [Fact]
    public void NoAnalyze_ReproducesGameGrainOnlyRecord()
    {
        var change = Compose(analyzeInline: false);

        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        // No board replay: no positions and no ANALYZED_AT watermark.
        Assert.DoesNotContain(change.Entities, e => e.TypeId == ChessVocabulary.PositionType);

        // The movetext DOES carry geometry even here, and that is the record layer, not the
        // calculated one: its trajectory is the order of the source's own tokens. What
        // --no-analyze withholds is the replay, so the only physicality is the movetext's.
        var movetextGeometry = change.Physicalities
            .Count(p => change.Entities.Any(e => e.Id == p.EntityId
                                              && e.TypeId == ChessVocabulary.MovetextType));
        Assert.Equal(change.Physicalities.Length, movetextGeometry);
        Assert.DoesNotContain(change.Attestations, a =>
            a.TypeId == RelationTypeRegistry.RelationTypeId("ANALYZED_AT"));
    }
}
