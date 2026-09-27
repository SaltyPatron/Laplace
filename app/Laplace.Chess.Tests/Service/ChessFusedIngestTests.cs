using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

// Chess ingest composes the recorded layer and the calculated layer (positions, move edges,
// analysis-version watermark) in one compose pass over the in-memory parse. These pin that
// both layers arrive in the same change, and that --no-analyze yields the recorded layer only.
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposedPhysicalitiesRetainEveryOwningSourcePrior(bool analyzeInline)
    {
        var change = Compose(analyzeInline);
        Assert.NotEmpty(change.Physicalities);
        var declared = new Dictionary<Hash128, double>
        {
            [ChessVocabulary.PgnSourceId] = SourceTrust.StructuredCorpus,
            [ChessAnalyze.SourceId] = SourceTrust.StructuredCorpus,
            [ChessTransitions.SourceId] = SourceTrust.StructuredCorpus,
            [ChessPositionOutcomes.SourceId] = SourceTrust.StructuredCorpus,
            [ChessTacticOutcomes.SourceId] = SourceTrust.StructuredCorpus,
            [ChessVocabulary.TrajectorySourceId] = SourceTrust.StructuredCorpus,
            [ChessSyzygy.SourceId] = SourceTrust.StandardsDerived,
        };
        var observedSources = new HashSet<Hash128>();
        void VerifySource(Hash128 sourceId)
        {
            Assert.True(declared.TryGetValue(sourceId, out var expected),
                $"unexpected physicality source {sourceId}");
            Assert.Equal(expected, change.RequireSourcePrior(sourceId));
            observedSources.Add(sourceId);
        }

        try
        {
            Assert.All(change.Physicalities, row => VerifySource(row.SourceId));
            foreach (var stage in change.IntentStages)
            {
                int covered = 0;
                foreach (var range in stage.PhysicalitySourceRanges)
                {
                    Assert.Equal(covered, range.FirstRow);
                    Assert.True(range.RowCount > 0);
                    VerifySource(range.SourceId);
                    covered = checked(covered + range.RowCount);
                }
                Assert.Equal(stage.PhysicalityCount, covered);
            }

            Assert.Contains(change.Physicalities,
                row => row.SourceId == ChessVocabulary.PgnSourceId);
            if (analyzeInline)
            {
                // Analysis content is emitted through native stages; the ordered
                // position projection carries the analyzer's source.
                Assert.Contains(ChessAnalyze.SourceId, observedSources);
                Assert.Contains(change.Physicalities,
                    row => row.SourceId == ChessVocabulary.TrajectorySourceId
                        && row.Type == PhysicalityType.Projection);
            }
        }
        finally
        {
            foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }

    [Fact]
    public void FusedCompose_EmitsWitnessedAndDerivedLayersTogether()
    {
        var change = Compose(analyzeInline: true);

        // Recorded layer: the line carries one ordered typed-move trajectory.
        Assert.Contains(change.Entities, e => e.TypeId == ChessVocabulary.GameType);
        var lineId = Assert.Single(change.Entities, e => e.TypeId == ChessVocabulary.GameType).Id;
        Assert.Contains(change.Physicalities,
            p => p.EntityId == lineId && p.Type == PhysicalityType.Content);
        Assert.DoesNotContain(change.Attestations,
            a => a.TypeId == RelationTypeRegistry.RelationTypeId("HAS_MOVETEXT"));

        // Calculated layer in the same change: the line trajectory references ordinary typed
        // position content. The perfcache only speeds composition; every trajectory child is
        // still a canonical entity with its physicality.
        var positions = change.Entities
            .Where(e => e.TypeId == ChessVocabulary.PositionType)
            .Select(e => e.Id)
            .ToHashSet();
        Assert.NotEmpty(positions);
        Assert.False(change.Physicalities.IsDefaultOrEmpty || change.Physicalities.Length == 0,
            "fused pass must compose position geometry");
        Assert.Contains(change.Attestations, a =>
            a.TypeId == ChessVocabulary.AnalysisVersionMetaTypeId);

        // The calculated pass deposits the line's projection trajectory over exactly the
        // position set, each position with its content physicality.
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
