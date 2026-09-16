using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

public sealed class ChessPositionPlayingObservationTests
{
    [Theory]
    [InlineData("parsed")]
    [InlineData("shared-replay")]
    [InlineData("witnessed")]
    [InlineData("live-trajectory")]
    public void CompleteGamesShareContentAndKeepDistinctReplayStableObservationIdentities(string path)
    {
        CodepointPerfcache.LoadDefault();
        var games = new[] { 1, 2 }.Select(i => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", $"position-playing-game-{i}.pgn")))
            .Select(pgn => Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn))).ToArray();
        Assert.Equal(2, games.Length);
        Assert.NotEqual(games[0].PlayingId, games[1].PlayingId);
        Assert.Equal(games[0].LineId, games[1].LineId);
        var modality = new ChessModality();
        foreach (var game in games)
        {
            Assert.Equal(154, game.ResolvedMoves.Length);
            var state = modality.Initial();
            foreach (var move in game.ResolvedMoves) state = modality.Apply(state, move);
            Assert.Equal(game.Result.ResultToken, modality.Terminal(state)?.ResultToken);
        }

        var first = Deposit(games[0], path);
        var second = Deposit(games[1], path);
        var replay = Deposit(games[0], path);
        try
        {
            var firstRows = first.Attestations.OrderBy(a => a.SubjectId.ToString()).ToArray();
            var secondRows = second.Attestations.OrderBy(a => a.SubjectId.ToString()).ToArray();
            Assert.NotEmpty(firstRows);
            Assert.Equal(firstRows.Select(a => a.SubjectId), secondRows.Select(a => a.SubjectId));
            Assert.All(firstRows, a => Assert.Equal(games[0].PlayingId, a.ContextId));
            Assert.All(secondRows, a => Assert.Equal(games[1].PlayingId, a.ContextId));
            Assert.Empty(firstRows.Select(a => a.Id).Intersect(secondRows.Select(a => a.Id)));
            Assert.Equal(firstRows.Select(a => (a.TypeId, a.ObjectId, a.SourceId, a.ObservationCount, a.SumScoreFp1e9)),
                secondRows.Select(a => (a.TypeId, a.ObjectId, a.SourceId, a.ObservationCount, a.SumScoreFp1e9)));
            Assert.Equal(first.Metadata.IntentId, replay.Metadata.IntentId);
            Assert.Equal(first.Attestations.Select(a => a with { LastObservedAtUnixUs = 0 }),
                replay.Attestations.Select(a => a with { LastObservedAtUnixUs = 0 }));
        }
        finally
        {
            foreach (var change in new[] { first, second, replay })
                foreach (var stage in change.IntentStages) stage.Dispose();
        }
    }

    [Theory]
    [InlineData("parsed")]
    [InlineData("shared-replay")]
    [InlineData("witnessed")]
    [InlineData("live-trajectory")]
    public void NativeBoardBatchesMatchScalarEvidenceForEveryFullGamePosition(string path)
    {
        CodepointPerfcache.LoadDefault();
        for (int fixture = 1; fixture <= 2; fixture++)
        {
            var pgn = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", $"position-playing-game-{fixture}.pgn"));
            var game = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn));
            var replay = ChessPgnDecomposer.MaterializeParsedReplay(game);
            Assert.Equal(154, game.ResolvedMoves.Length);
            Assert.Equal(155, replay.Positions.Length);
            var expected = DepositScalar(game, replay, out long occurrences);
            SubstrateChange? actual = null;
            try
            {
                long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
                actual = Deposit(game, path);
                long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + 999;
                AssertChangesEqual(expected, actual);
                Assert.Equal(occurrences, actual.Attestations.Sum(row => row.ObservationCount));
                Assert.Contains(actual.Attestations, row => row.ObservationCount > 1);
                Assert.All(actual.Attestations, row =>
                {
                    Assert.True(row.FoldReplayable);
                    Assert.InRange(row.LastObservedAtUnixUs, Math.Min(before, after), Math.Max(before, after));
                });
            }
            finally
            {
                foreach (var stage in expected.IntentStages) stage.Dispose();
                if (actual is not null)
                    foreach (var stage in actual.IntentStages) stage.Dispose();
            }
        }
    }

    [Fact]
    public void NativeBoardBatchesKeepOccurrencesWhenALegalLineReturnsToItsInitialBoard()
    {
        CodepointPerfcache.LoadDefault();
        const string pgn = "[Event \"batch-repeat\"]\n[White \"White\"]\n[Black \"Black\"]\n[Result \"1/2-1/2\"]\n\n1. Nf3 Nf6 2. Ng1 Ng8 1/2-1/2";
        var game = Assert.IsType<ChessGameRecord>(ChessPgnDecomposer.TryParseGame(pgn));
        var replay = ChessPgnDecomposer.MaterializeParsedReplay(game);
        Assert.Equal(5, replay.Positions.Length);
        Assert.Equal(game.PositionIds[0], game.PositionIds[^1]);
        var expected = DepositScalar(game, replay, out long occurrences);
        SubstrateChange? actual = null;
        try
        {
            actual = Deposit(game, "shared-replay");
            AssertChangesEqual(expected, actual);
            Assert.Equal(occurrences, actual.Attestations.Sum(row => row.ObservationCount));
            foreach (var atom in replay.Positions[0].Substructures)
                Assert.True(Assert.Single(actual.Attestations,
                    row => row.SubjectId == atom.Id).ObservationCount >= 2);
        }
        finally
        {
            foreach (var stage in expected.IntentStages) stage.Dispose();
            if (actual is not null)
                foreach (var stage in actual.IntentStages) stage.Dispose();
        }
    }

    private static SubstrateChange DepositScalar(
        ChessGameRecord game, ChessParsedReplay replay, out long occurrences)
    {
        var builder = new SubstrateChangeBuilder(ChessPositionOutcomes.SourceId,
            "test/position-playing/" + game.PlayingId);
        occurrences = 0;
        long score = ChessGraph.ScoreFp1e9(game.Result.ForMover(0));
        foreach (var position in replay.Positions)
        {
            var composed = ChessGraph.EmitComposed(builder, position, ChessPositionOutcomes.SourceId);
            foreach (var atom in composed.Substructures)
            {
                builder.AddAttestation(NativeAttestation.Aggregated(atom.Id,
                    ChessVocabulary.OutcomeType, ChessVocabulary.OutcomeObject,
                    ChessPositionOutcomes.SourceId, game.PlayingId, 1, score, 0.9));
                occurrences++;
            }
        }
        builder.AddEntity(ChessPositionOutcomes.MarkerId(game.PlayingId), EntityTier.Document,
            ChessVocabulary.AnalysisMarkerType, ChessPositionOutcomes.SourceId);
        return builder.SetInputUnitsConsumed(1).Build();
    }

    private static void AssertChangesEqual(SubstrateChange expected, SubstrateChange actual)
    {
        Assert.Equal(expected.Metadata with { BuiltAt = default }, actual.Metadata with { BuiltAt = default });
        // ImmutableArray<T>.Equals compares its backing array identity. Require
        // every ordered record value, with a specific index on a real mismatch.
        Assert.Equal(expected.Entities.Length, actual.Entities.Length);
        for (int i = 0; i < expected.Entities.Length; i++)
            Assert.True(expected.Entities[i] == actual.Entities[i],
                $"Entity index {i}: expected {expected.Entities[i]}, actual {actual.Entities[i]}");
        Assert.Equal(expected.Attestations.Select(row => row with { LastObservedAtUnixUs = 0 }),
            actual.Attestations.Select(row => row with { LastObservedAtUnixUs = 0 }));
        Assert.Equal(expected.PhysicalitySourcePriors.OrderBy(row => row.Key.ToString()),
            actual.PhysicalitySourcePriors.OrderBy(row => row.Key.ToString()));
        AssertPhysicalitiesEqual(expected.Physicalities, actual.Physicalities);
        AssertPhysicalitiesEqual(expected.PhysicalityObservations, actual.PhysicalityObservations);
        Assert.Equal(expected.CanonicalNames.IsDefault, actual.CanonicalNames.IsDefault);
        if (!expected.CanonicalNames.IsDefault)
        {
            Assert.Equal(expected.CanonicalNames.Length, actual.CanonicalNames.Length);
            for (int i = 0; i < expected.CanonicalNames.Length; i++)
                Assert.True(string.Equals(expected.CanonicalNames[i], actual.CanonicalNames[i],
                    StringComparison.Ordinal),
                    $"Canonical name index {i}: expected '{expected.CanonicalNames[i]}', actual '{actual.CanonicalNames[i]}'");
        }
    }

    private static void AssertPhysicalitiesEqual(
        IReadOnlyList<PhysicalityRow> expected, IReadOnlyList<PhysicalityRow> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(PhysicalityBody(expected[i]), PhysicalityBody(actual[i]));
            Assert.Equal(expected[i].TrajectoryXyzm?.Select(BitConverter.DoubleToInt64Bits),
                actual[i].TrajectoryXyzm?.Select(BitConverter.DoubleToInt64Bits));
        }
    }

    // Compare exact payload bits and source attribution; processing timestamps are
    // intentionally different across independent scalar and batch executions.
    private static object PhysicalityBody(PhysicalityRow row) => new
    {
        row.Id, row.EntityId, row.SourceId, row.Type,
        X = BitConverter.DoubleToInt64Bits(row.CoordX),
        Y = BitConverter.DoubleToInt64Bits(row.CoordY),
        Z = BitConverter.DoubleToInt64Bits(row.CoordZ),
        M = BitConverter.DoubleToInt64Bits(row.CoordM),
        row.HilbertIndex, row.NConstituents, row.SourceDim,
        Alignment = row.AlignmentResidual is { } alignment
            ? (long?)BitConverter.DoubleToInt64Bits(alignment) : null
    };

    private static SubstrateChange Deposit(ChessGameRecord game, string path)
    {
        var builder = new SubstrateChangeBuilder(ChessPositionOutcomes.SourceId,
            "test/position-playing/" + game.PlayingId);
        var modality = new ChessModality();
        switch (path)
        {
            case "parsed": ChessPositionOutcomes.DepositFromParsed(builder, game); break;
            case "shared-replay": ChessPositionOutcomes.DepositFromParsed(builder, game,
                ChessPgnDecomposer.MaterializeParsedReplay(game)); break;
            case "witnessed": ChessPositionOutcomes.Deposit(builder, ChessAnalyze.WitnessedFromParsed(game)); break;
            case "live-trajectory": ChessPositionOutcomes.DepositTrajectory(builder,
                // Live hosts retain the modality interchange surface, not FEN text.
                ChessPgnDecomposer.MaterializeParsedReplay(game).Boards
                    .Select(board => modality.StateKey(new ChessState(board))).ToArray(),
                game.Result, game.PlayingId); break;
            default: throw new ArgumentOutOfRangeException(nameof(path));
        }
        return builder.SetInputUnitsConsumed(1).Build();
    }
}
