using Laplace.Engine.Core;
using Laplace.Decomposers.Abstractions;
using Laplace.Modality;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// The analyzer composes one ordered line physicality from deterministic perfcache move points,
// without expanding them into position/substructure trees or per-ply projections.
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

        Assert.Equal(direct.Entities.OrderBy(e => e.Id.ToString()),
            viaWitness.Entities.OrderBy(e => e.Id.ToString()));
        Assert.Equal(direct.Attestations.OrderBy(a => a.Id.ToString())
                .Select(a => a with { LastObservedAtUnixUs = 0 }),
            viaWitness.Attestations.OrderBy(a => a.Id.ToString())
                .Select(a => a with { LastObservedAtUnixUs = 0 }));
        var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(direct.Physicalities
                .OrderBy(p => p.Id.ToString()).Select(p => p with { ObservedAtUnixUs = 0 }), json),
            System.Text.Json.JsonSerializer.Serialize(viaWitness.Physicalities
                .OrderBy(p => p.Id.ToString()).Select(p => p with { ObservedAtUnixUs = 0 }), json));
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

    [Theory]
    [InlineData(Game)]
    [InlineData("[Event \"Setup\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Result \"1-0\"]\n[SetUp \"1\"]\n[FEN \"7k/5KP1/8/8/8/8/8/8 w - - 0 1\"]\n\n1. g8=Q# 1-0\n")]
    [InlineData("[Event \"Resigned\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Result \"1-0\"]\n\n1-0\n")]
    public void SharedParsedReplay_PreservesAllCalculatedRows(string pgn)
    {
        CodepointPerfcache.LoadDefault();
        var parsed = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn));
        var replay = ChessPgnDecomposer.MaterializeParsedReplay(parsed);
        Assert.True(replay.IsCompleteFor(parsed));
        Assert.Equal(parsed.PositionIds, replay.Positions.Select(p => p.Position.Id));
        var boardStates = replay.Boards.Select(board => board.ToFen()).ToArray();

        var separate = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/lab-analysis");
        ChessAnalyze.DeriveFromParsed(separate, parsed);
        ChessTransitions.DepositFromParsed(separate, parsed);
        ChessPositionOutcomes.DepositFromParsed(separate, parsed);
        var expected = separate.Build();
        SubstrateChange? actual = null;
        try
        {
            var shared = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/lab-analysis");
            ChessAnalyze.DeriveFromParsed(shared, parsed, replay);
            ChessTransitions.DepositFromParsed(shared, parsed);
            ChessPositionOutcomes.DepositFromParsed(shared, parsed, replay);
            actual = shared.Build();

            Assert.Equal(expected.Metadata.IntentId, actual.Metadata.IntentId);
            // ImmutableArray.Equals compares backing arrays; the sequence overload
            // compares every row in order.
            Assert.Equal<EntityRow>(expected.Entities, actual.Entities);
            Assert.Equal(expected.Attestations.Select(a => a with { LastObservedAtUnixUs = 0 }),
                actual.Attestations.Select(a => a with { LastObservedAtUnixUs = 0 }));
            var json = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
            Assert.Equal(
                System.Text.Json.JsonSerializer.Serialize(expected.Physicalities
                    .Select(p => p with { ObservedAtUnixUs = 0 }), json),
                System.Text.Json.JsonSerializer.Serialize(actual.Physicalities
                    .Select(p => p with { ObservedAtUnixUs = 0 }), json));
            Assert.Equal(IntentStage.SemanticDigestBatch(expected.IntentStages),
                IntentStage.SemanticDigestBatch(actual.IntentStages));
            Assert.Equal<EphemeralFoldInput>(expected.EphemeralFoldInputs, actual.EphemeralFoldInputs);
            Assert.Empty(expected.TestimonyWalks);
            Assert.Empty(actual.TestimonyWalks);
            Assert.Equal(boardStates, replay.Boards.Select(board => board.ToFen()));
            Assert.Equal(parsed.PositionIds, replay.Positions.Select(p => p.Position.Id));
        }
        finally
        {
            foreach (var stage in expected.IntentStages) stage.Dispose();
            if (actual is not null)
                foreach (var stage in actual.IntentStages) stage.Dispose();
        }
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
    public void ThinkPace_ScopesTheClassToTheMoveLineAndPlayer()
    {
        CodepointPerfcache.LoadDefault();
        var b = new SubstrateChangeBuilder(ChessVocabulary.AnalysisSourceId, "test/think");
        var transition = Hash128.Text("test/transition");
        var line = Hash128.Text("test/line");
        var player = Hash128.Text("test/player");
        var playing = Hash128.Text("test/playing");
        ChessGraph.AppendThinkPace(
            b, transition, line, player, playing, "rushed", 0.7, ChessVocabulary.AnalysisSourceId);
        var change = b.Build();
        var classId = ContentEmitter.RootId("rushed");
        Assert.NotNull(classId);

        foreach (var subject in new[] { transition, line, player })
        {
            Assert.Contains(change.Attestations,
                a => a.SubjectId == subject
                     && a.TypeId == ChessVocabulary.HasThinkClassType
                     && a.ObjectId == classId
                     && a.ContextId == playing);
        }

        Assert.DoesNotContain(change.Attestations,
            a => a.SubjectId == classId.Value && a.ContextId is null);
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == ChessVocabulary.OutcomeType && a.SubjectId == classId.Value);
    }

    [Fact]
    public void ThinkPace_FollowsThePlayedGame()
    {
        const string clocked =
            "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Result \"1-0\"]\n\n"
            + "1. e4 {[%clk 0:05:00]} e5 {[%clk 0:05:00]} "
            + "2. Qh5 {[%clk 0:04:50]} Nc6 {[%clk 0:04:55]} "
            + "3. Bc4 {[%clk 0:04:40]} Nf6 {[%clk 0:04:50]} "
            + "4. Qxf7# {[%clk 0:04:30]} 1-0\n";
        var parsed = ChessPgnDecomposer.TryParseGame(clocked)!;
        var change = Analyze(clocked);
        var pace = change.Attestations
            .Where(a => a.TypeId == ChessVocabulary.HasThinkClassType)
            .ToList();
        Assert.NotEmpty(pace);
        Assert.All(pace, a => Assert.Equal(parsed.PlayingId, a.ContextId));
        Assert.Contains(pace, a => a.SubjectId == parsed.LineId);
        var white = ChessVocabulary.PlayerId("Alice");
        var black = ChessVocabulary.PlayerId("Bob");
        Assert.Contains(pace, a => a.SubjectId == white);
        Assert.Contains(pace, a => a.SubjectId == black);
        Assert.Contains(pace, a =>
            a.SubjectId != parsed.LineId && a.SubjectId != white && a.SubjectId != black);

        var classIds = pace.Select(a => a.ObjectId).ToHashSet();
        Assert.DoesNotContain(change.Attestations,
            a => a.ContextId is null && classIds.Contains(a.SubjectId));
    }

    [Fact]
    public void ThinkPace_FollowsSpentTimeComments()
    {
        const string spent =
            "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Result \"1-0\"]\n\n"
            + "1. e4 {+0.28/12 0.95s} e5 {-0.21/14 1.02s} "
            + "2. Qh5 {+0.35/13 0.98s} Nc6 {-0.30/15 3.50s} "
            + "3. Bc4 {+1.05/10 0.05s} Nf6 {+8.41/12 1.00s} "
            + "4. Qxf7# {+M0/5 0.10s} 1-0\n";
        var parsed = ChessPgnDecomposer.TryParseGame(spent)!;
        var change = Analyze(spent);
        var pace = change.Attestations
            .Where(a => a.TypeId == ChessVocabulary.HasThinkClassType)
            .ToList();
        Assert.NotEmpty(pace);
        Assert.All(pace, a => Assert.Equal(parsed.PlayingId, a.ContextId));
        Assert.Contains(pace, a => a.SubjectId == parsed.LineId);
        Assert.Contains(pace, a => a.SubjectId == ChessVocabulary.PlayerId("Alice"));
        Assert.Contains(pace, a => a.SubjectId == ChessVocabulary.PlayerId("Bob"));
        var classIds = pace.Select(a => a.ObjectId).ToHashSet();
        Assert.DoesNotContain(change.Attestations,
            a => a.ContextId is null && a.TypeId == ChessVocabulary.OutcomeType
                 && classIds.Contains(a.SubjectId));
    }

    [Fact]
    public void Analyzer_StampsAnalysisMarker()
    {
        var change = Analyze(Game);
        var parsed = ChessPgnDecomposer.TryParseGame(Game)!;
        // The analyzer's unit is the playing, so the analysis marker keys on the playing id,
        // the same id ChessAnalyze stamps.
        var marker = ChessVocabulary.AnalysisMarkerId(parsed.PlayingId, ChessAnalyze.Version);
        Assert.Contains(change.Entities, e => e.Id == marker && e.TypeId == ChessVocabulary.AnalysisMarkerType);
    }
}
