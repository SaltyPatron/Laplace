using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// The analyzer builds one ordered line physicality from deterministic perfcache move points.
// It must not expand those points into SQL position/substructure trees or per-ply projections.
public sealed class ChessAnalyzerTests
{
    private const string Game =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    private static SubstrateChange Analyze(string pgn)
    {
        CodepointPerfcache.LoadDefault();
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var b = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/analysis");
        ChessAnalyze.DeriveFromParsed(b, parsed);
        return b.SetInputUnitsConsumed(1).Build();
    }

    [Fact]
    public void WitnessedFromParsed_MatchesDirectDeriveOutput()
    {
        CodepointPerfcache.LoadDefault();
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var direct = Analyze(Game);
        var b = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/witnessed");
        ChessAnalyze.DeriveFromWitnessed(b, ChessAnalyze.WitnessedFromParsed(parsed));
        var viaWitness = b.SetInputUnitsConsumed(1).Build();

        Assert.Equal(direct.Entities.Length, viaWitness.Entities.Length);
        Assert.Equal(direct.Physicalities.Length, viaWitness.Physicalities.Length);
        Assert.DoesNotContain(viaWitness.Entities, e => e.TypeId == ChessVocabulary.PositionType);
    }

    [Fact]
    public void ParsedReplay_ReusesParsedPositions_AndStagesOutcomePositionTrees()
    {
        CodepointPerfcache.LoadDefault();
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        var replay = ChessPgnDecomposer.MaterializeParsedReplay(parsed);

        Assert.True(replay.IsCompleteFor(parsed));
        Assert.Equal(parsed.PositionIds, replay.Positions.Select(p => p.Position.Id).ToArray());

        var b = new SubstrateChangeBuilder(ChessPositionOutcomes.SourceId, "test/position-outcomes/replay");
        ChessPositionOutcomes.DepositFromParsed(b, parsed, replay);
        var change = b.SetInputUnitsConsumed(1).Build();

        foreach (var position in replay.Positions)
            Assert.Contains(change.Entities,
                e => e.Id == position.Position.Id && e.TypeId == ChessVocabulary.PositionType);
        Assert.Contains(change.Entities,
            e => e.Id == ChessPositionOutcomes.MarkerId(parsed.PlayingId));
    }

    [Fact]
    public void Analyzer_EmitsOneLineTrajectoryWithoutPositionTrees()
    {
        var change = Analyze(Game);
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;

        Assert.DoesNotContain(change.Entities, e => e.TypeId == ChessVocabulary.PositionType);
        Assert.DoesNotContain(change.Entities, e => e.TypeId == ChessVocabulary.SubstructureType);
        var trajectory = Assert.Single(change.Physicalities,
            p => p.EntityId == parsed.LineId && p.Type == PhysicalityType.Projection);
        Assert.Equal(parsed.Moves.Count + 1, trajectory.NConstituents);
        Assert.Equal(parsed.PositionIds, Trajectory.Constituents(trajectory.TrajectoryXyzm!));
    }

    [Fact]
    public void Analyzer_KeepsExactTransitionsInTrajectory_NotConsensus()
    {
        var change = Analyze(Game);
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == ChessVocabulary.MoveType);

        foreach (var relation in new[]
                 {
                     "HAS_CLOCK", "HAS_EVAL_TOKEN", "HAS_THINK_CLASS", "MOVE_QUALITY", "HAS_MOTIF"
                 })
        {
            var typeId = RelationTypeRegistry.RelationTypeId(relation);
            Assert.DoesNotContain(change.Attestations, a => a.TypeId == typeId);
        }
    }

    [Fact]
    public void ThinkClass_FoldsOutcomeAtReusableClassGrain()
    {
        CodepointPerfcache.LoadDefault();
        var b = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/think");
        ChessGraph.AppendThinkOutcome(
            b, "rushed", PlyOutcome.Win, 0.7, ChessVocabulary.AnalysisSourceId);
        var change = b.Build();
        var classId = ContentEmitter.RootId("rushed");
        Assert.NotNull(classId);

        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == ChessVocabulary.HasThinkClassType);
        Assert.Contains(change.Attestations,
            a => a.SubjectId == classId.Value
                 && a.TypeId == ChessVocabulary.OutcomeType
                 && a.ObjectId == ChessVocabulary.OutcomeObject
                 && a.ContextId is null);
    }

    [Fact]
    public void Analyzer_StampsAnalysisMarker()
    {
        var change = Analyze(Game);
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        // GH #736: the analyzer's unit is the PLAYING, so the marker keys on the playing —
        // the same id ChessAnalyze stamps. Keying it on the event made this probe miss.
        var marker = ChessVocabulary.AnalysisMarkerId(parsed.PlayingId, ChessAnalyze.Version);
        Assert.Contains(change.Entities, e => e.Id == marker && e.TypeId == ChessVocabulary.AnalysisMarkerType);
    }
}
