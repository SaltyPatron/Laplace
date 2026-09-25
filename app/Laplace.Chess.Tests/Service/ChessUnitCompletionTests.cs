using System.Reflection;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessUnitCompletionTests
{
    private const string Game =
        "[Event \"Receipt\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n"
        + "[Result \"1-0\"]\n\n1. e4 e5 1-0\n";

    [Theory]
    [InlineData("analysis", 21)]
    [InlineData("transitions", 22)]
    [InlineData("positions", 22)]
    [InlineData("tactics", 24)]
    [InlineData("player-context", 25)]
    [InlineData("trajectory", 21)]
    [InlineData("opening-no-hit", 21)]
    [InlineData("moves-inline", 22)]
    public void CompleteOwnerDepositsCarryExactOperationalProof(string lane, int layer)
    {
        CodepointPerfcache.LoadDefault();
        var parsed = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(Game));
        var game = ChessAnalyze.WitnessedFromParsed(parsed);
        var (owner, marker, compose) = Plan(lane, parsed);
        using var builder = new SubstrateChangeBuilder(owner, "test/unit/" + lane)
            .DeclareSourcePrior(SourceTrust.StructuredCorpus);
        compose(builder, game);
        var change = builder.Build();
        try
        {
            // Completion is operational state beside the evidence: exactly one unit
            // completion owned by the lane's witness, and no completion attestation.
            Hash128? context = lane == "opening-no-hit" ? new EmptyOpeningIndex().GenerationId : null;
            var proof = Assert.Single(change.UnitCompletions,
                key => key.WitnessId == owner && key.UnitId == marker);
            Assert.Equal(new IngestUnitCompletionKey(owner, marker, layer, context), proof);
            Assert.Empty(change.LayerCompletions);
            Assert.DoesNotContain(change.Attestations,
                row => row.SubjectId == marker && row.ObjectId == marker);
            Assert.Contains(change.Entities,
                row => row.Id == marker && row.TypeId == ChessVocabulary.AnalysisMarkerType);
        }
        finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
    }

    [Theory]
    [InlineData("analysis", 21)]
    [InlineData("transitions", 22)]
    [InlineData("positions", 22)]
    [InlineData("tactics", 24)]
    [InlineData("player-context", 25)]
    [InlineData("trajectory", 21)]
    [InlineData("opening-no-hit", 21)]
    public void FailedReplayCannotIssueCompletionOverItsPartialOutput(string lane, int layer)
    {
        CodepointPerfcache.LoadDefault();
        var parsed = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(Game));
        var valid = ChessAnalyze.WitnessedFromParsed(parsed);
        var (owner, marker, compose) = Plan(lane, parsed);
        foreach (var game in new[]
                 {
                     valid with { Moves = new[] { "e4", "invalid-SAN" } },
                     valid with { StartFen = "invalid-FEN" },
                 })
        {
            using var builder = new SubstrateChangeBuilder(owner, "test/incomplete/" + lane)
                .DeclareSourcePrior(SourceTrust.StructuredCorpus);
            compose(builder, game);
            var change = builder.Build();
            try
            {
                Assert.Empty(change.UnitCompletions);
                Assert.Empty(change.Attestations);
                Assert.Empty(change.Entities);
                Assert.Empty(change.Physicalities);
            }
            finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
        }
    }

    [Fact]
    public void BookCompletionBindsActualCommentaryWithoutChangingTheSemanticRoot()
    {
        CodepointPerfcache.LoadDefault();
        var first = Assert.Single(ChessBookDecomposer.ExtractFromText(Game, "Receipt book"));
        var second = first with { Context = "A distinct explanation of this same playing." };
        Assert.Equal(first.TrunkRootId, second.TrunkRootId);
        Assert.NotEqual(first.Completion, second.Completion);

        var compose = typeof(ChessBookDecomposer).GetMethod("Compose",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var record in new[] { first, second })
        {
            using var builder = new SubstrateChangeBuilder(ChessVocabulary.BookSourceId, "test/book-receipt")
                .DeclareSourcePrior(SourceTrust.AcademicCurated);
            compose.Invoke(new ChessBookDecomposer(), [record, builder]);
            var change = builder.Build();
            try
            {
                var receipt = Assert.Single(change.UnitCompletions,
                    key => key.WitnessId == ChessVocabulary.BookSourceId && key.Layer == 20);
                Assert.Equal(record.Completion, receipt);
                Assert.Equal(record.RootId, receipt.UnitId);
                Assert.Equal(record.CompletionContextId, receipt.Digest);
                Assert.Equal(ChessVocabulary.BookSourceId, receipt.WitnessId);
            }
            finally { foreach (var stage in change.IntentStages) stage.Dispose(); }
        }
    }

    private static (Hash128 Owner, Hash128 Marker, Action<SubstrateChangeBuilder, ChessWitnessedGame> Compose)
        Plan(string lane, ChessGameRecord parsed) => lane switch
        {
            "analysis" => (ChessAnalyze.SourceId,
                ChessVocabulary.AnalysisMarkerId(parsed.PlayingId, ChessAnalyze.Version),
                (builder, game) => { ChessAnalyze.DeriveFromWitnessed(builder, game); }),
            "transitions" => (ChessTransitions.SourceId, ChessTransitions.MarkerId(parsed.PlayingId),
                ChessTransitions.Deposit),
            "positions" => (ChessPositionOutcomes.SourceId, ChessPositionOutcomes.MarkerId(parsed.PlayingId),
                ChessPositionOutcomes.Deposit),
            "tactics" => (ChessTacticOutcomes.SourceId, ChessTacticOutcomes.MarkerId(parsed.PlayingId),
                ChessTacticOutcomes.DeriveGame),
            "player-context" => (ChessPlayerContextOutcomes.SourceId,
                ChessPlayerContextOutcomes.MarkerId(parsed.PlayingId), ChessPlayerContextOutcomes.DeriveGame),
            "trajectory" => (ChessVocabulary.TrajectorySourceId,
                ChessTrajectoryDecomposer.MarkerId(parsed.LineId),
                (builder, game) => ChessTrajectoryDecomposer.Deposit(builder, game, ChessVocabulary.TrajectorySourceId)),
            "opening-no-hit" => (ChessVocabulary.OpeningMatchSourceId,
                ChessOpeningMatchDecomposer.MarkerId(parsed.LineId),
                (builder, game) => ChessOpeningMatchDecomposer.Match(
                    builder, game, new EmptyOpeningIndex(), ChessVocabulary.OpeningMatchSourceId)),
            "moves-inline" => (ChessVocabulary.PgnSourceId,
                ChessMoveOutcomes.MarkerId(parsed.LineId, ChessMoveOutcomes.Version),
                (builder, game) => ChessMoveOutcomes.AppendGame(
                    builder, parsed.LineId, parsed.MoveIds, parsed.Result,
                    parsed.InitialWhiteToMove!.Value, ChessVocabulary.PgnSourceId, 0.9)),
            _ => throw new ArgumentOutOfRangeException(nameof(lane)),
        };

    private sealed class EmptyOpeningIndex : ChessOpeningIndexView
    {
        public Hash128? GenerationId => Hash128.OfCanonical("test/unit/empty-opening-catalog/v1");
        public (Hash128 NameId, Hash128? EcoId, int Ply)? DeepestMatch(IReadOnlyList<Hash128> positions) => null;
    }
}
