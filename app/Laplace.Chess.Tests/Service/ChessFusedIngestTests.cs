using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// GH #600: `laplace ingest chess` records the witnessed layer AND derives the calculated
// layer (positions, move edges, analysis-version watermark) in ONE fused Compose pass, reusing the
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

        // Witnessed layer: shared line owns one ordered typed-move trajectory.
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        var lineId = Assert.Single(change.Entities, e => e.TypeId == ChessVocabulary.GameType).Id;
        Assert.Contains(change.Physicalities,
            p => p.EntityId == lineId && p.Type == PhysicalityType.Content);
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_MOVETEXT"));

        // Derived layer present in the SAME change: the line trajectory references ordinary
        // typed position content. The perfcache accelerates composition; it does not replace
        // the canonical entities/physicalities or leave trajectory children unresolved.
        var positions = change.Entities
            .Where(e => e.TypeId == ChessVocabulary.PositionType)
            .Select(e => e.Id)
            .ToHashSet();
        Assert.NotEmpty(positions);
        Assert.False(change.Physicalities.IsDefaultOrEmpty || change.Physicalities.Length == 0,
            "fused pass must compose position geometry");
        Assert.Contains(change.Attestations, a =>
            a.TypeId == ChessVocabulary.AnalysisVersionMetaTypeId);

        // GH #547 / CONSOLIDATION: EmitGame stays a bare Document (spec 08 recorder);
        // the fused calculated pass deposits the game-tier mantissa-packed trajectory
        // on the LINE. Closing the claim that "EmitGame deposits a bare Document" as
        // the whole story — bare on record, trajectory on analyze/fusion.
        var gameTraj = Assert.Single(change.Physicalities,
            p => p.EntityId == lineId && p.Type == PhysicalityType.Projection);
        Assert.NotNull(gameTraj.TrajectoryXyzm);
        Assert.True(gameTraj.NConstituents > 0);
        Assert.Equal(Trajectory.Constituents(gameTraj.TrajectoryXyzm!).ToHashSet(), positions);
        Assert.All(positions, id => Assert.Single(change.Physicalities,
            p => p.EntityId == id && p.Type == PhysicalityType.Content));
    }

    [Fact]
    public void NoAnalyze_ReproducesGameGrainOnlyRecord()
    {
        var change = Compose(analyzeInline: false);

        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        // No board replay: no positions and no analysis-version watermark.
        Assert.DoesNotContain(change.Entities, e => e.TypeId == ChessVocabulary.PositionType);

        // Recording keeps reusable moves and the line trajectory; --no-analyze withholds
        // the calculated line-of-positions trajectory.
        var lineId = Assert.Single(change.Entities, e => e.TypeId == ChessVocabulary.GameType).Id;
        Assert.Contains(change.Physicalities,
            p => p.EntityId == lineId && p.Type == PhysicalityType.Content);
        Assert.DoesNotContain(change.Physicalities,
            p => p.EntityId == lineId && p.Type == PhysicalityType.Projection);
        Assert.DoesNotContain(change.Attestations, a =>
            a.TypeId == ChessVocabulary.AnalysisVersionMetaTypeId);
    }
}
